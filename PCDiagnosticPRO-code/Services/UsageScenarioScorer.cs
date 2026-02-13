using System;
using System.Collections.Generic;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Scores 8 usage scenarios 0-100 and assigns classification (Not Recommended / Acceptable / Good / Excellent).
    /// When a PerformanceDataset is provided, scenario rules and classification thresholds are read from it.
    /// When dataset is null, all formulas use the original hardcoded constants (backward compat).
    /// </summary>
    public static class UsageScenarioScorer
    {
        // Hardcoded classification bands (used when dataset is null):
        // Not Recommended <40, Acceptable 40-55, Good 56-70, Excellent >70
        private const int ThresholdNotRecommended = 40;
        private const int ThresholdAcceptable = 55;
        private const int ThresholdGood = 70;

        /// <summary>
        /// Score all 8 scenarios for the given hardware profile.
        /// Priority: MarketBenchmarks (specs-vs-market comparison) → ScenarioRules (base+bonus) → hardcoded fallback.
        /// </summary>
        public static List<ScenarioScore> Score(HardwareProfile profile, PerformanceDataset? dataset = null)
        {
            // Prefer market benchmark scoring when available (specs vs market requirements)
            if (dataset?.MarketBenchmarks != null && dataset.MarketBenchmarks.Count > 0)
                return ScoreFromMarketBenchmarks(profile, dataset);

            if (dataset != null && dataset.ScenarioRules != null && dataset.ScenarioRules.Count > 0)
                return ScoreFromDataset(profile, dataset);

            return ScoreHardcoded(profile);
        }

        #region Market benchmark scoring (specs vs market requirements — "Can You Run It" style)

        /// <summary>
        /// Score all scenarios by comparing the PC's actual specs against per-scenario market requirements.
        /// Each component (CPU, GPU, RAM, Storage) is scored individually by interpolating between min/recommended/ultra
        /// thresholds, then combined using configurable weights.
        /// Result: below min → 0-60 (proportional), at min → 60, at recommended → 80, at/above ultra (optimal) → 100.
        /// Now produces precise decimal scores for granular differentiation.
        /// </summary>
        private static List<ScenarioScore> ScoreFromMarketBenchmarks(HardwareProfile profile, PerformanceDataset ds)
        {
            var scenarios = new (string id, string name)[]
            {
                ("office", "Office / Browsing"),
                ("multitasking", "Multitasking"),
                ("gaming_1080p", "Gaming (1080p)"),
                ("gaming_1440p", "Gaming (1440p)"),
                ("gaming_4k", "Gaming (4K)"),
                ("4k_editing", "4K Video Editing"),
                ("streaming_gaming", "Streaming + Gaming"),
                ("vms", "Virtual Machines"),
                ("ai_inference", "AI (basic inference)")
            };

            var list = new List<ScenarioScore>();
            foreach (var (id, name) in scenarios)
            {
                if (ds.MarketBenchmarks!.TryGetValue(id, out var bench))
                {
                    var (preciseScore, explanation) = ScoreAgainstMarketWithExplanation(profile, bench.Requirements, name);
                    double clampedScore = ClampDouble(preciseScore);
                    list.Add(new ScenarioScore
                    {
                        ScenarioId = id,
                        Name = name,
                        PreciseScore = Math.Round(clampedScore, 1),
                        Classification = ClassifyFromDataset((int)Math.Round(clampedScore), ds.ClassificationThresholds),
                        Explanation = explanation
                    });
                }
                else
                {
                    // Fallback to ScenarioRules (base+bonus) if this scenario has no market benchmark
                    if (ds.ScenarioRules != null && ds.ScenarioRules.TryGetValue(id, out var rule))
                    {
                        int score = EvaluateScenarioRule(profile, rule);
                        // Add micro-variance to avoid flat numbers
                        double preciseScore = score + GetMicroVariance(profile, id);
                        list.Add(new ScenarioScore
                        {
                            ScenarioId = id,
                            Name = name,
                            PreciseScore = Math.Round(ClampDouble(preciseScore), 1),
                            Classification = ClassifyFromDataset(Clamp(score), ds.ClassificationThresholds)
                        });
                    }
                    else
                    {
                        list.Add(new ScenarioScore { ScenarioId = id, Name = name, PreciseScore = 0, Classification = ScenarioClassification.NotRecommended });
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// Calculate a small variance based on hardware characteristics to differentiate otherwise identical scores.
        /// </summary>
        private static double GetMicroVariance(HardwareProfile p, string scenarioId)
        {
            // Use hardware values to create unique but small variance
            double variance = 0;
            variance += (p.CpuCores % 10) * 0.12;
            variance += (p.CpuThreads % 10) * 0.08;
            variance += ((int)(p.GpuVramMb / 1024) % 10) * 0.15;
            variance += ((int)p.RamGb % 10) * 0.1;
            // Add scenario-specific variance
            variance += (scenarioId.GetHashCode() % 100) * 0.005;
            return Math.Min(variance, 4.9); // Cap at 4.9 to avoid tier boundary crossing
        }

        /// <summary>
        /// Score a single scenario by comparing the PC's specs to market requirements.
        /// Returns precise decimal score and explanation.
        /// Now includes: CPU frequency scoring, video acceleration bonus, and LLM RAM bonus.
        /// </summary>
        private static (double score, ScoreExplanation explanation) ScoreAgainstMarketWithExplanation(
            HardwareProfile p, ScenarioRequirements req, string scenarioName)
        {
            int gpuTierOrder = PerformanceTierTable.TierOrder(p.GpuTier);
            int storageTier = GetStorageTierOrder(p.StorageKind);

            var explanation = new ScoreExplanation
            {
                Method = "MarketBenchmark",
                Source = "External Dataset (market requirements)"
            };

            // ── CPU score: cores + threads, optionally combined with frequency ──
            double cpuCoresScore = Interpolate3(p.CpuCores, req.MinCpuCores, req.RecommendedCpuCores, req.UltraCpuCores);
            double cpuThreadsScore = Interpolate3(p.CpuThreads, req.MinCpuThreads, req.RecommendedCpuThreads, req.UltraCpuThreads);
            double cpuCoresThreadsScore = (cpuCoresScore + cpuThreadsScore) / 2.0;
            double cpuScore = cpuCoresThreadsScore;

            // If GHz requirements are defined, integrate frequency into CPU score
            double actualGhz = Math.Max(p.CpuBaseGhz, p.CpuBoostGhz);
            if (req.UltraCpuGhz > 0 && actualGhz > 0)
            {
                // Full GHz interpolation when all three levels are defined
                double cpuGhzScore = Interpolate3(actualGhz, req.MinCpuGhz, req.RecommendedCpuGhz, req.UltraCpuGhz);
                // Combine: 60% cores/threads + 40% frequency
                cpuScore = (cpuCoresThreadsScore * 0.6) + (cpuGhzScore * 0.4);
                
                explanation.Components.Add(ScoreComponent.Weighted(
                    "CPU", $"{p.CpuCores}c/{p.CpuThreads}t @ {actualGhz:F1} GHz", p.CpuCores, cpuScore, req.WeightCpu));
            }
            else
            {
                explanation.Components.Add(ScoreComponent.Weighted(
                    "CPU", $"{p.CpuCores}c/{p.CpuThreads}t", p.CpuCores, cpuScore, req.WeightCpu));
            }

            // ── GPU score: tier + VRAM ──
            double gpuTierScore = Interpolate3(gpuTierOrder, req.MinGpuTierOrder, req.RecommendedGpuTierOrder, req.UltraGpuTierOrder);
            double gpuVramScore = (req.UltraGpuVramMb > 0)
                ? Interpolate3(p.GpuVramMb, req.MinGpuVramMb, req.RecommendedGpuVramMb, req.UltraGpuVramMb)
                : gpuTierScore;
            double gpuScore = (req.UltraGpuVramMb > 0) ? (gpuTierScore + gpuVramScore) / 2.0 : gpuTierScore;

            explanation.Components.Add(ScoreComponent.Weighted(
                "GPU", $"{p.GpuVramMb:F0} Mo VRAM", p.GpuVramMb, gpuScore, req.WeightGpu));

            // ── RAM score ──
            double ramScore = Interpolate3(p.RamGb, req.MinRamGb, req.RecommendedRamGb, req.UltraRamGb);
            explanation.Components.Add(ScoreComponent.Weighted(
                "RAM", $"{p.RamGb:F0} Go", p.RamGb, ramScore, req.WeightRam));

            // ── Storage score ──
            double storageScore = Interpolate3(storageTier, req.MinStorageTier, req.RecommendedStorageTier, req.UltraStorageTier);
            explanation.Components.Add(ScoreComponent.Weighted(
                "Stockage", p.StorageKind ?? "Unknown", storageTier, storageScore, req.WeightStorage));

            // ── Weighted average (base score) ──
            double totalWeight = req.WeightCpu + req.WeightGpu + req.WeightRam + req.WeightStorage;
            if (totalWeight <= 0) totalWeight = 1.0;

            double baseScore = (cpuScore * req.WeightCpu
                               + gpuScore * req.WeightGpu
                               + ramScore * req.WeightRam
                               + storageScore * req.WeightStorage) / totalWeight;

            // ══════════════════════════════════════════════════════════════════
            // EXPLICIT BONUSES (traceable, capped)
            // ══════════════════════════════════════════════════════════════════
            double bonusTotal = 0;
            const double MaxBonusPerCategory = 15.0; // Cap per bonus type

            // ── Bonus 1: CPU Frequency (when MinCpuGhz is set but not full interpolation) ──
            // This applies when the PC exceeds the minimum GHz requirement
            if (req.MinCpuGhz > 0 && actualGhz > req.MinCpuGhz && req.UltraCpuGhz <= 0)
            {
                // Proportional bonus: how much above minimum (capped at +15)
                double excessRatio = (actualGhz - req.MinCpuGhz) / req.MinCpuGhz;
                double ghzBonus = Math.Min(MaxBonusPerCategory, MaxBonusPerCategory * excessRatio);
                ghzBonus = Math.Round(ghzBonus, 1);
                
                if (ghzBonus > 0)
                {
                    bonusTotal += ghzBonus;
                    explanation.Components.Add(ScoreComponent.Bonus(
                        "Bonus fréquence CPU",
                        $"+{ghzBonus:F1} ({actualGhz:F1} GHz > {req.MinCpuGhz:F1} GHz min)",
                        ghzBonus));
                }
            }

            // ── Bonus 2: Video Acceleration (for scenarios requiring video playback) ──
            if (req.RequiresVideoAcceleration)
            {
                // Award bonus if GPU has VRAM (indicates video-capable GPU) or is at least Entry tier
                bool hasVideoCapability = p.GpuVramMb > 0 || gpuTierOrder >= 1;
                if (hasVideoCapability)
                {
                    // Scale bonus based on GPU capability: Entry=+5, Mid+=+8, Upper Mid+=+10
                    double videoBonus = gpuTierOrder >= 3 ? 10.0 : (gpuTierOrder >= 2 ? 8.0 : 5.0);
                    bonusTotal += videoBonus;
                    explanation.Components.Add(ScoreComponent.Bonus(
                        "Capacités vidéo",
                        $"+{videoBonus:F0} (accélération matérielle détectée)",
                        videoBonus));
                }
            }

            // ── Bonus 3: RAM for LLM (AI inference scenarios) ──
            if (req.MinRamGbForLLM > 0 && p.RamGb >= req.MinRamGbForLLM)
            {
                // Proportional bonus based on how much RAM exceeds LLM minimum (capped at +15)
                double ramExcessRatio = (p.RamGb - req.MinRamGbForLLM) / req.MinRamGbForLLM;
                double llmRamBonus = 10.0 + Math.Min(5.0, 5.0 * ramExcessRatio); // Base +10, up to +15
                llmRamBonus = Math.Round(llmRamBonus, 1);
                
                bonusTotal += llmRamBonus;
                explanation.Components.Add(ScoreComponent.Bonus(
                    "RAM suffisante LLM",
                    $"+{llmRamBonus:F1} ({p.RamGb:F0} Go >= {req.MinRamGbForLLM:F0} Go)",
                    llmRamBonus));
            }

            // ══════════════════════════════════════════════════════════════════
            // FINAL SCORE (base + bonuses, clamped to 0-100)
            // ══════════════════════════════════════════════════════════════════
            double finalScore = ClampDouble(baseScore + bonusTotal);

            explanation.FinalScore = Math.Round(finalScore, 1);
            explanation.Confidence = MatchConfidence.High;

            return (finalScore, explanation);
        }

        /// <summary>
        /// Legacy method for backward compatibility.
        /// </summary>
        private static int ScoreAgainstMarket(HardwareProfile p, ScenarioRequirements req)
        {
            var (score, _) = ScoreAgainstMarketWithExplanation(p, req, "");
            return (int)Math.Round(score);
        }

        /// <summary>
        /// Three-point interpolation: maps actual value to a 0-100 score.
        /// Below min → 0 to 60 (proportional to distance to minimum).
        /// At min → 60 (passing threshold).
        /// Between min and recommended → 60 to 80 (linear).
        /// Between recommended and optimal (ultra) → 80 to 100 (linear).
        /// At optimal or above → 100.
        /// </summary>
        private static double Interpolate3(double actual, double min, double recommended, double ultra)
        {
            // Handle edge case: if all thresholds are 0 (not applicable), return 100
            if (ultra <= 0 && recommended <= 0 && min <= 0) return 100.0;

            // Ensure min <= recommended <= ultra
            if (recommended < min) recommended = min;
            if (ultra < recommended) ultra = recommended;

            if (actual >= ultra) return 100.0;

            if (actual >= recommended)
            {
                // 80 → 100 between recommended and ultra
                double range = ultra - recommended;
                if (range <= 0) return 100.0;
                return 80.0 + 20.0 * (actual - recommended) / range;
            }

            if (actual >= min)
            {
                // 60 → 80 between min and recommended
                double range = recommended - min;
                if (range <= 0) return 80.0;
                return 60.0 + 20.0 * (actual - min) / range;
            }

            // Below min: 0 → 60 proportional to distance to minimum
            if (min <= 0) return 0.0;
            double ratio = actual / min;
            if (ratio < 0) ratio = 0;
            return 60.0 * ratio;
        }

        /// <summary>Convert storage kind string to tier order for interpolation.</summary>
        private static int GetStorageTierOrder(string? storageKind)
        {
            if (string.IsNullOrEmpty(storageKind)) return 0;
            if (string.Equals(storageKind, PerformanceTierTable.StorageNvme, StringComparison.OrdinalIgnoreCase)) return 4;
            if (string.Equals(storageKind, PerformanceTierTable.StorageSataSsd, StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(storageKind, PerformanceTierTable.StorageHdd, StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        #endregion

        #region Dataset-driven scoring

        private static List<ScenarioScore> ScoreFromDataset(HardwareProfile profile, PerformanceDataset ds)
        {
            var scenarios = new (string id, string name)[]
            {
                ("office", "Office / Browsing"),
                ("multitasking", "Multitasking"),
                ("gaming_1080p", "Gaming (1080p)"),
                ("gaming_1440p", "Gaming (1440p)"),
                ("gaming_4k", "Gaming (4K)"),
                ("4k_editing", "4K Video Editing"),
                ("streaming_gaming", "Streaming + Gaming"),
                ("vms", "Virtual Machines"),
                ("ai_inference", "AI (basic inference)")
            };

            var list = new List<ScenarioScore>();
            foreach (var (id, name) in scenarios)
            {
                if (ds.ScenarioRules.TryGetValue(id, out var rule))
                {
                    int score = EvaluateScenarioRule(profile, rule);
                    // Add micro-variance to avoid flat numbers
                    double preciseScore = score + GetMicroVariance(profile, id);
                    list.Add(new ScenarioScore
                    {
                        ScenarioId = id,
                        Name = name,
                        PreciseScore = Math.Round(ClampDouble(preciseScore), 1),
                        Classification = ClassifyFromDataset(Clamp(score), ds.ClassificationThresholds)
                    });
                }
                else
                {
                    // Missing rule in dataset — score 0, Not Recommended
                    list.Add(new ScenarioScore
                    {
                        ScenarioId = id,
                        Name = name,
                        PreciseScore = 0,
                        Classification = ScenarioClassification.NotRecommended
                    });
                }
            }
            return list;
        }

        /// <summary>
        /// Evaluate a single scenario rule: base + sum of applicable bonuses.
        /// Supports ElseIf chaining within bonus lists.
        /// </summary>
        private static int EvaluateScenarioRule(HardwareProfile p, ScenarioRule rule)
        {
            int score = rule.Base;
            bool previousApplied = false;

            for (int i = 0; i < rule.Bonuses.Count; i++)
            {
                var bonus = rule.Bonuses[i];

                // ElseIf: skip if previous bonus in chain was applied
                if (bonus.ElseIf && previousApplied)
                {
                    // Don't apply this bonus; keep previousApplied true for further chaining
                    continue;
                }

                bool met = EvaluateCondition(p, bonus.Condition);
                if (met)
                {
                    score += bonus.Points;
                    previousApplied = true;
                }
                else
                {
                    previousApplied = false;
                }
            }

            return score;
        }

        /// <summary>
        /// Evaluate a single condition string against a hardware profile.
        /// Supported forms:
        ///   CpuTierOrder>=N, GpuTierOrder>=N
        ///   RamGb>=N, GpuVramMb>=N, CpuThreads>=N, CpuCores>=N
        ///   StorageKind==HDD, StorageKind==NVMe, StorageKind==SATA_SSD
        /// </summary>
        private static bool EvaluateCondition(HardwareProfile p, string condition)
        {
            if (string.IsNullOrEmpty(condition)) return false;

            // Try "==" operator (equality)
            int eqIdx = condition.IndexOf("==", StringComparison.Ordinal);
            if (eqIdx > 0)
            {
                string field = condition.Substring(0, eqIdx).Trim();
                string value = condition.Substring(eqIdx + 2).Trim();
                return field switch
                {
                    "StorageKind" => string.Equals(p.StorageKind, value, StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
            }

            // Try ">=" operator (greater-or-equal)
            int geIdx = condition.IndexOf(">=", StringComparison.Ordinal);
            if (geIdx > 0)
            {
                string field = condition.Substring(0, geIdx).Trim();
                string valStr = condition.Substring(geIdx + 2).Trim();
                if (!double.TryParse(valStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double threshold))
                    return false;

                return field switch
                {
                    "CpuTierOrder" => PerformanceTierTable.TierOrder(p.CpuTier) >= threshold,
                    "GpuTierOrder" => PerformanceTierTable.TierOrder(p.GpuTier) >= threshold,
                    "RamGb" => p.RamGb >= threshold,
                    "GpuVramMb" => p.GpuVramMb >= threshold,
                    "CpuThreads" => p.CpuThreads >= threshold,
                    "CpuCores" => p.CpuCores >= threshold,
                    _ => false
                };
            }

            return false;
        }

        private static string ClassifyFromDataset(int score, ClassificationThresholds? thresholds)
        {
            var t = thresholds ?? new ClassificationThresholds();
            if (score < t.NotRecommendedBelow) return ScenarioClassification.NotRecommended;
            if (score < t.AcceptableBelow) return ScenarioClassification.Acceptable;
            if (score < t.GoodBelow) return ScenarioClassification.Good;
            return ScenarioClassification.Excellent;
        }

        #endregion

        #region Hardcoded scoring (original — backward compat fallback)

        private static List<ScenarioScore> ScoreHardcoded(HardwareProfile profile)
        {
            // Calculate base scores with hardcoded formulas, then add micro-variance
            var scenarios = new List<ScenarioScore>
            {
                ScoreOfficeBrowsing(profile),
                ScoreMultitasking(profile),
                ScoreGaming1080p(profile),
                ScoreGaming1440p(profile),
                ScoreGaming4k(profile),
                Score4KVideoEditing(profile),
                ScoreStreamingGaming(profile),
                ScoreVirtualMachines(profile),
                ScoreAIBasicInference(profile)
            };

            // Add micro-variance to each score for differentiation
            foreach (var s in scenarios)
            {
                double preciseScore = s.Score + GetMicroVariance(profile, s.ScenarioId);
                s.PreciseScore = Math.Round(ClampDouble(preciseScore), 1);
            }

            return scenarios;
        }

        private static string Classify(int score)
        {
            string classification;
            if (score < ThresholdNotRecommended) classification = ScenarioClassification.NotRecommended;
            else if (score < ThresholdAcceptable) classification = ScenarioClassification.Acceptable;
            else if (score < ThresholdGood) classification = ScenarioClassification.Good;
            else classification = ScenarioClassification.Excellent;
            return classification;
        }

        /// <summary>Office/Browsing: CPU >= Entry, RAM >= 8GB; HDD penalized.</summary>
        private static ScenarioScore ScoreOfficeBrowsing(HardwareProfile p)
        {
            int score = 50;
            if (PerformanceTierTable.TierOrder(p.CpuTier) >= 1) score += 25;
            if (p.RamGb >= 8) score += 15;
            if (p.RamGb >= 16) score += 5;
            if (p.StorageKind == PerformanceTierTable.StorageHdd) score += -15;
            else if (p.StorageKind == PerformanceTierTable.StorageNvme) score += 5;
            return new ScenarioScore
            {
                ScenarioId = "office",
                Name = "Office / Browsing",
                Score = Clamp(score),
                Classification = Classify(Clamp(score))
            };
        }

        /// <summary>Multitasking: CPU Mid preferred, RAM >= 16GB.</summary>
        private static ScenarioScore ScoreMultitasking(HardwareProfile p)
        {
            int score = 30;
            if (PerformanceTierTable.TierOrder(p.CpuTier) >= 2) score += 25;
            if (PerformanceTierTable.TierOrder(p.CpuTier) >= 3) score += 15;
            if (p.RamGb >= 16) score += 25;
            if (p.RamGb >= 32) score += 5;
            return new ScenarioScore { ScenarioId = "multitasking", Name = "Multitasking", Score = Clamp(score), Classification = Classify(Clamp(score)) };
        }

        /// <summary>1080p Gaming: GPU >= Mid, VRAM >= 6GB, RAM >= 16GB.</summary>
        private static ScenarioScore ScoreGaming1080p(HardwareProfile p)
        {
            int score = 40;
            if (PerformanceTierTable.TierOrder(p.GpuTier) >= 2) score += 30;
            else if (PerformanceTierTable.TierOrder(p.GpuTier) >= 1) score += 15;
            if (p.GpuVramMb >= 6144) score += 20;
            else if (p.GpuVramMb >= 4096) score += 10;
            if (p.RamGb >= 16) score += 10;
            else if (p.RamGb >= 8) score += 5;
            return new ScenarioScore { ScenarioId = "gaming_1080p", Name = "Gaming (1080p)", Score = Clamp(score), Classification = Classify(Clamp(score)) };
        }

        /// <summary>1440p Gaming: GPU Upper Mid/High, VRAM >= 8GB, RAM >= 16GB.</summary>
        private static ScenarioScore ScoreGaming1440p(HardwareProfile p)
        {
            int score = 20;
            if (PerformanceTierTable.TierOrder(p.GpuTier) >= 4) score += 40;
            else if (PerformanceTierTable.TierOrder(p.GpuTier) >= 3) score += 30;
            else if (PerformanceTierTable.TierOrder(p.GpuTier) >= 2) score += 15;
            if (p.GpuVramMb >= 8192) score += 25;
            else if (p.GpuVramMb >= 6144) score += 15;
            if (p.RamGb >= 16) score += 15;
            return new ScenarioScore { ScenarioId = "gaming_1440p", Name = "Gaming (1440p)", Score = Clamp(score), Classification = Classify(Clamp(score)) };
        }

        /// <summary>4K Gaming: more demanding than 1440p — GPU High-end, VRAM >= 12GB, RAM >= 32GB.</summary>
        private static ScenarioScore ScoreGaming4k(HardwareProfile p)
        {
            int score = 0;
            if (PerformanceTierTable.TierOrder(p.GpuTier) >= 5) score += 45;
            else if (PerformanceTierTable.TierOrder(p.GpuTier) >= 4) score += 35;
            else if (PerformanceTierTable.TierOrder(p.GpuTier) >= 3) score += 20;
            if (p.GpuVramMb >= 24576) score += 30;
            else if (p.GpuVramMb >= 16384) score += 25;
            else if (p.GpuVramMb >= 12288) score += 15;
            if (p.RamGb >= 32) score += 25;
            else if (p.RamGb >= 16) score += 10;
            return new ScenarioScore { ScenarioId = "gaming_4k", Name = "Gaming (4K)", Score = Clamp(score), Classification = Classify(Clamp(score)) };
        }

        /// <summary>4K Video Editing: CPU High, RAM >= 32GB, GPU capable, fast storage.</summary>
        private static ScenarioScore Score4KVideoEditing(HardwareProfile p)
        {
            int score = 0;
            if (PerformanceTierTable.TierOrder(p.CpuTier) >= 4) score += 30;
            else if (PerformanceTierTable.TierOrder(p.CpuTier) >= 3) score += 20;
            if (p.RamGb >= 32) score += 30;
            else if (p.RamGb >= 16) score += 15;
            if (PerformanceTierTable.TierOrder(p.GpuTier) >= 2) score += 20;
            if (p.StorageKind == PerformanceTierTable.StorageNvme) score += 20;
            else if (p.StorageKind == PerformanceTierTable.StorageSataSsd) score += 10;
            return new ScenarioScore { ScenarioId = "4k_editing", Name = "4K Video Editing", Score = Clamp(score), Classification = Classify(Clamp(score)) };
        }

        /// <summary>Streaming + Gaming: CPU + GPU both strong, RAM >= 16GB.</summary>
        private static ScenarioScore ScoreStreamingGaming(HardwareProfile p)
        {
            int score = 25;
            if (PerformanceTierTable.TierOrder(p.CpuTier) >= 3) score += 25;
            else if (PerformanceTierTable.TierOrder(p.CpuTier) >= 2) score += 15;
            if (PerformanceTierTable.TierOrder(p.GpuTier) >= 3) score += 25;
            else if (PerformanceTierTable.TierOrder(p.GpuTier) >= 2) score += 15;
            if (p.RamGb >= 16) score += 25;
            return new ScenarioScore { ScenarioId = "streaming_gaming", Name = "Streaming + Gaming", Score = Clamp(score), Classification = Classify(Clamp(score)) };
        }

        /// <summary>Virtual Machines: CPU cores/threads, RAM >= 16GB (32GB for multiple VMs).</summary>
        private static ScenarioScore ScoreVirtualMachines(HardwareProfile p)
        {
            int score = 20;
            if (p.CpuThreads >= 16) score += 35;
            else if (p.CpuThreads >= 8) score += 25;
            else if (p.CpuThreads >= 4) score += 15;
            if (p.RamGb >= 32) score += 35;
            else if (p.RamGb >= 16) score += 25;
            else if (p.RamGb >= 8) score += 10;
            return new ScenarioScore { ScenarioId = "vms", Name = "Virtual Machines", Score = Clamp(score), Classification = Classify(Clamp(score)) };
        }

        /// <summary>AI (basic inference): GPU VRAM >= 6GB, RAM >= 16GB.</summary>
        private static ScenarioScore ScoreAIBasicInference(HardwareProfile p)
        {
            int score = 20;
            if (p.GpuVramMb >= 8192) score += 40;
            else if (p.GpuVramMb >= 6144) score += 30;
            else if (p.GpuVramMb >= 4096) score += 20;
            if (p.RamGb >= 32) score += 20;
            else if (p.RamGb >= 16) score += 15;
            return new ScenarioScore { ScenarioId = "ai_inference", Name = "AI (basic inference)", Score = Clamp(score), Classification = Classify(Clamp(score)) };
        }

        #endregion

        private static int Clamp(int score)
        {
            if (score < 0) return 0;
            if (score > 100) return 100;
            return score;
        }

        private static double ClampDouble(double score)
        {
            if (score < 0) return 0;
            if (score > 100) return 100;
            return score;
        }
    }
}
