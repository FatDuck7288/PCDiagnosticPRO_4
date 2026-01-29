#Requires -Version 5.1
<#
.SYNOPSIS
    Script de collecte diagnostic Windows 10/11 - Sonde brute pour pipeline IA
.DESCRIPTION
    COLLECTE UNIQUEMENT - Aucune analyse, scoring ou interpretation.
    Genere un rapport TXT humain + JSON structure pour traitement externe.
    STRICTEMENT LECTURE SEULE - Aucune modification systeme.
    
.NOTES
    Version: 7.0
    Auteur: Data Next Step / Alexandre
    Compatibilite: PowerShell 5.1 Desktop
    
    CHANGELOG v7.0 (VERSION FINALE BRIQUE 1):
    =========================================
    [FINAL] Version stabilisee pour integration C# WPF Virtual IT Pro
    [NEUTRALISE] Temperature CPU: Non disponible (limitation WMI - collecte externalisee)
    [NEUTRALISE] Temperature GPU: Non disponible (limitation WMI - collecte externalisee)
    [NEUTRALISE] VRAM via AdapterRAM: Non disponible (limitation WMI - collecte externalisee)
    [SCORE] Les limitations techniques ne penalisent plus le score
    [SCORE] Score minimum 10, grade base sur problemes REELS uniquement
    [GPU] Section conservee avec donnees fiables (nom, fabricant, driver, resolution)
    [TEMPERATURES] Disques conserves si disponibles, CPU/GPU neutralises
    [ROBUSTESSE] Aucune erreur bloquante, best effort systematique
    
    CHANGELOG v6.6.6:
    =================
    [STABLE] Version stabilisee pour integration C# WPF
    [ROBUSTESSE] Validation renforcee Extract-SmartTemperature (plage 0-125)
    [FALLBACK] DynamicSignals: validation post-collecte + WMI fallback complet
    [TRACE] Source documentee sur CPU/RAM/Disk/SMART
    [SCORE] Score min 10, penalites ponderees, topPenalties[] inclus
    
    CHANGELOG v6.6.5:
    =================
    [PATCH 1] Load-LibreHardwareMonitor: chemins Virtual IT Pro + fallback WMI explicite
    [PATCH 2] Calculate-ScoreV2: penalites ponderees, score min 10, top 5 penalites
    [PATCH 3] Stopwatch global: duree console = duree rapport (unifiee)
    [PATCH 4] SMART: Extract-SmartTemperature low-byte ameliore (deja v6.6.3)
    [TRACE] Source documentee sur collectes critiques
    [LICENCE] Note MPL 2.0 pour LibreHardwareMonitor
    
    CHANGELOG v6.6.4:
    =================
    [PATCH 1] InstalledApplications: ajout HKCU + fallback Get-Package
    [PATCH 2] Processes: fallback CIM Win32_Process si Get-Process echoue
    [PATCH 3] PowerSettings: meilleur parsing powercfg + fallback registre
    [PATCH 4] DynamicSignals: fallback CIM si Get-Counter echoue
    [PATCH 5] WMI_ERROR: messages detailles (namespace/classe/exception)
    [TRACE] Source documentee sur chaque collecte critique
    
    CHANGELOG v6.6.3:
    =================
    [PATCH 1] Normalize-Temperature: conversion Kelvin auto (200-500K, 1000-5000 dK)
    [PATCH 2] Extract-SmartTemperature: lecture low byte SMART (attributs 194/190)
    [PATCH 3] Add-WmiError: journalisation detaillee WMI/CIM (namespace/classe/methode)
    [PATCH 4] Test-ValidValue: distingue 0 (valide) de null/empty (echec)
    [PATCH 5] Fallback temperature disque: SMART -> StorageReliability -> unavailable
    
    CHANGELOG v6.6.2:
    =================
    [ROBUSTESSE] Invoke-WithFallback: execution multi-methodes avec fallback auto
    [FALLBACK] Temperatures: LHM -> WMI ThermalZone -> registry -> unavailable
    [FALLBACK] SMART: MSFT_StorageReliability -> Win32_DiskDrive -> unavailable
    [FALLBACK] WMI: Get-CimInstance -> Get-WmiObject -> unavailable
    [QUALITE] Champs source/quality/reason sur donnees critiques
    [TRACE] Tous echecs loggues dans missingData[]
    
    CHANGELOG v6.6.1:
    =================
    [CRITIQUE] Correction bug JSON "if n'est pas reconnu" (31 occurrences)
      - Syntaxe "$var = if (...) {...} else {...}" remplacee par if/else classique
      - Toutes les expressions inline corrigees pour PS 5.1 strict
    [AJOUT] IA-readiness: champs normalises abnormal/reason/confidence/key_metrics
    [AJOUT] Tableau global missingData[] pour donnees non collectees
    [AJOUT] ScoreV2 avec breakdown detaille (CRIT -25, ERROR -10, WARN -3)
    [ROBUSTESSE] Logging JSON ameliore si echec serialisation
    [COMPAT] PowerShell 5.1 Desktop strict (pas d'operateur ternaire)
    
    CHANGELOG v6.6.0:
    =================
    [v6.6.0] Patches "produit final":
      - Wrapper global anti-crash + generation TXT/JSON minimal garantie.
      - Contrat JSON immuable (metadata/paths/sections/errors/findings) + conversion JSON safe.
      - VRAM GPU multi-sources priorisee (NVIDIA-SMI/DXDIAG/REGISTRY/WMI) avec validation stricte.
      - Ajout temperature CPU + disques best-effort avec bornes de plausibilite.
      - Temperatures/SMART: rejet des lectures absurdes et notes explicites.
      - Monitoring dynamique durci (tri CPU sans crash, garde-fous sur acces propriete).
      - Statuts de sections stables meme en cas d'echec collecteur.
      - Redaction/encodage UTF-8 BOM conserves pour compatibilite C#.

.PARAMETER Full
    Active export JSON complet dans le rapport TXT
.PARAMETER MaxEvents
    Nombre max d'evenements par journal (defaut: 50)
.PARAMETER SkipPreflightCheck
    Ignore verification Execution Policy
.PARAMETER NoRedact
    Desactive le masquage des donnees sensibles
.PARAMETER RedactLevel
    Niveau de redaction (Basic, Full). Defaut: Full
.PARAMETER OutputDir
    Repertoire de sortie pour les rapports
.PARAMETER QuickScan
    Mode scan rapide (3 secondes de monitoring)
.PARAMETER MonitorSeconds
    Duree du monitoring dynamique (3-60, defaut: 10)
.PARAMETER AllowExternalNetworkTests
    Autorise les tests reseau externes (ping/DNS). Par defaut: desactive
.PARAMETER NetworkTestTargets
    Liste des cibles ping pour les tests reseau externes
.PARAMETER DnsTestTargets
    Liste des domaines pour les tests DNS externes
.PARAMETER ExternalCommandTimeoutSeconds
    Timeout (secondes) pour les commandes externes (dxdiag, nvidia-smi)
.PARAMETER MaxTempFiles
    Limite max de fichiers temp analyzes (0 = illimite)
.PARAMETER HardenOutputAcl
    Applique un durcissement ACL sur le dossier de sortie
#>

#region [LICENSE]
# Ce script intègre la bibliothèque **LibreHardwareMonitorLib.dll** distribuée sous licence MPL 2.0.
# La licence MPL 2.0 est disponible à l'adresse : https://www.mozilla.org/MPL/2.0/.
# Conformément aux exigences de licence, cette sonde de température est identifiée comme le « Module de température Virtual IT Pro »
# et ne se revendique pas comme le produit officiel LibreHardwareMonitor.
#endregion

[CmdletBinding()]
param(
    [switch]$Full,
    [int]$MaxEvents = 50,
    [switch]$SkipPreflightCheck,
    [switch]$NoRedact,
    [ValidateSet('Basic', 'Full')]
    [string]$RedactLevel = 'Full',
    [string]$OutputDir = "C:\Virtual IT Pro\Rapport",
    [switch]$QuickScan,
    [ValidateRange(3, 60)]
    [int]$MonitorSeconds = 10,
    [switch]$AllowExternalNetworkTests,
    [string[]]$NetworkTestTargets = @('8.8.8.8', '1.1.1.1'),
    [string[]]$DnsTestTargets = @('google.com', 'microsoft.com'),
    [ValidateRange(1, 300)]
    [int]$ExternalCommandTimeoutSeconds = 20,
    [int]$MaxTempFiles = 0,
    [switch]$HardenOutputAcl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$PSDefaultParameterValues['Set-Content:Encoding'] = 'utf8'
$PSDefaultParameterValues['Add-Content:Encoding'] = 'utf8'

try {
    # Forcer l'encodage UTF-8 pour la console et les flux de sortie
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

#region ============== CONFIGURATION ==============
$Script:Config = @{
    SchemaVersion = '7.0'
    Limits = @{ MaxEvents = 50 }
    DynamicSignals = @{
        DefaultSeconds = 10; QuickSeconds = 3
        MinSeconds = 3; MaxSeconds = 60; SampleInterval = 1
    }
}
#endregion

#region ============== VARIABLES GLOBALES ==============
$Script:ScriptVersion = "7.0"
$Script:RunId = [Guid]::NewGuid().ToString()
$Script:StartTime = Get-Date
$Script:GlobalStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$Script:IsAdmin = $false
$Script:ReportLines = [System.Collections.Generic.List[string]]::new()
$Script:SectionData = [ordered]@{}
$Script:WmiCache = @{}
$Script:ErrorLog = [System.Collections.Generic.List[object]]::new()
$Script:Findings = @()
$Script:MissingData = [System.Collections.Generic.List[object]]::new()
$Script:RedactCache = @{}
$Script:CollectorStatus = [ordered]@{}
$Script:PartialFailure = $false
$Script:SectionKeys = @(
    'MachineIdentity','OS','CPU','Memory','Storage','GPU','Network','Security','Services','StartupPrograms',
    'HealthChecks','EventLogs','WindowsUpdate','Audio','DevicesDrivers','InstalledApplications','ScheduledTasks',
    'Processes','Battery','Printers','UserProfiles','Virtualization','RestorePoints','TempFiles','EnvironmentVariables',
    'Certificates','Registry','Temperatures','SystemIntegrity','PowerSettings','MinidumpAnalysis','ReliabilityHistory',
    'PerformanceCounters','NetworkLatency','SmartDetails','DynamicSignals','AdvancedAnalysis'
)
$Script:SectionNameMap = @{
    MachineIdentity = 'Identite Machine'
    OS = 'Systeme Exploitation'
    CPU = 'Processeur'
    Memory = 'Memoire'
    Storage = 'Stockage'
    GPU = 'Carte Graphique'
    Network = 'Reseau'
    Security = 'Securite'
    Services = 'Services'
    StartupPrograms = 'Demarrage'
    HealthChecks = 'Health Checks'
    EventLogs = 'Journaux Evenements'
    WindowsUpdate = 'Windows Update'
    Audio = 'Audio'
    DevicesDrivers = 'Peripheriques'
    InstalledApplications = 'Applications'
    ScheduledTasks = 'Taches Planifiees'
    Processes = 'Processus'
    Battery = 'Batterie'
    Printers = 'Imprimantes'
    UserProfiles = 'Profils Utilisateurs'
    Virtualization = 'Virtualisation'
    RestorePoints = 'Points Restauration'
    TempFiles = 'Fichiers Temporaires'
    EnvironmentVariables = 'Variables Environnement'
    Certificates = 'Certificats'
    Registry = 'Registre'
    Temperatures = 'Temperatures'
    SystemIntegrity = 'Integrite Systeme'
    PowerSettings = 'Alimentation'
    MinidumpAnalysis = 'Analyse Minidumps'
    ReliabilityHistory = 'Historique Fiabilite'
    PerformanceCounters = 'Compteurs Performance'
    NetworkLatency = 'Latence Reseau'
    SmartDetails = 'SMART Detaille'
    DynamicSignals = 'DynamicSignals'
    AdvancedAnalysis = 'AdvancedAnalysis'
}

$Script:NoRedact = $NoRedact.IsPresent
$Script:RedactLevel = $RedactLevel
$Script:QuickScan = $QuickScan.IsPresent

if ($Script:QuickScan) { 
    $Script:MonitorSeconds = $Script:Config.DynamicSignals.QuickSeconds 
} else { 
    $Script:MonitorSeconds = [math]::Max($Script:Config.DynamicSignals.MinSeconds, [math]::Min($MonitorSeconds, $Script:Config.DynamicSignals.MaxSeconds))
}

$Script:OutputDir = $OutputDir
try {
    if (-not (Test-Path $Script:OutputDir)) {
        $null = New-Item -ItemType Directory -Path $Script:OutputDir -Force -ErrorAction SilentlyContinue
    }
} catch { }

$timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$Script:OutputPath = Join-Path $Script:OutputDir "Scan_$($Script:RunId)_$timestamp.txt"
$Script:JsonOutputPath = Join-Path $Script:OutputDir "Scan_$($Script:RunId)_$timestamp.json"
$Script:ReportWritten = $false
$Script:JsonWritten = $false
$Script:RedactionStats = @{ TotalRedactions = 0 }
$Script:AllowExternalNetworkTests = $AllowExternalNetworkTests.IsPresent
$Script:NetworkTestTargets = $NetworkTestTargets
$Script:DnsTestTargets = $DnsTestTargets
$Script:ExternalCommandTimeoutSeconds = [math]::Max(1, $ExternalCommandTimeoutSeconds)
$Script:MaxTempFiles = $MaxTempFiles
$Script:HardenOutputAcl = $HardenOutputAcl.IsPresent
#endregion

#region ============== HELPERS ROBUSTES (v6.4) ==============
<#
.SYNOPSIS
    Recupere une propriete sur un objet de facon 100% securisee
.DESCRIPTION
    Ne throw JAMAIS. Retourne $Default si:
    - $Object est $null
    - La propriete n'existe pas
    - La valeur est $null
#>
function Get-SafePropValue {
    param(
        [object]$Object,
        [string]$PropertyName,
        [object]$Default = $null
    )
    try {
        if ($null -eq $Object) { return $Default }
        if ([string]::IsNullOrEmpty($PropertyName)) { return $Default }
        
        # Pour les PSCustomObject et objets .NET
        if ($Object.PSObject -and $Object.PSObject.Properties) {
            $prop = $Object.PSObject.Properties[$PropertyName]
            if ($null -ne $prop) {
                $val = $prop.Value
                if ($null -ne $val) { return $val }
            }
        }
        return $Default
    }
    catch { return $Default }
}

<#
.SYNOPSIS
    Recupere une valeur dans un dictionnaire/hashtable de facon 100% securisee
.DESCRIPTION
    Gere: Hashtable, OrderedDictionary, Dictionary<>, IDictionary
    Ne throw JAMAIS.
#>
function Get-SafeDictValue {
    param(
        [object]$Dict,
        [string]$Key,
        [object]$Default = $null
    )
    try {
        if ($null -eq $Dict) { return $Default }
        if ([string]::IsNullOrEmpty($Key)) { return $Default }
        
        # Verifier si c'est un dictionnaire
        if ($Dict -is [System.Collections.IDictionary]) {
            # Utiliser Contains() qui marche pour tous les IDictionary
            if ($Dict.Contains($Key)) {
                $val = $Dict[$Key]
                if ($null -ne $val) { return $val }
            }
        }
        return $Default
    }
    catch { return $Default }
}

<#
.SYNOPSIS
    Compte les elements d'une collection de facon 100% securisee
.DESCRIPTION
    Ne throw JAMAIS. Retourne 0 si:
    - $Collection est $null
    - $Collection n'a pas de Count
    - Erreur quelconque
#>
function Get-SafeCount {
    param([object]$Collection)
    try {
        if ($null -eq $Collection) { return 0 }
        
        # Forcer en array pour avoir .Count fiable
        $arr = @($Collection)
        return $arr.Count
    }
    catch { return 0 }
}

<#
.SYNOPSIS
    Verifie si une cle existe dans un dictionnaire de facon securisee
#>
function Test-SafeHasKey {
    param([object]$Dict, [string]$Key)
    try {
        if ($null -eq $Dict) { return $false }
        if ([string]::IsNullOrEmpty($Key)) { return $false }
        if ($Dict -is [System.Collections.IDictionary]) {
            return $Dict.Contains($Key)
        }
        return $false
    }
    catch { return $false }
}
function Convert-SafeLong {
    param([object]$Value, [long]$Default = 0)
    if ($null -eq $Value) { return $Default }
    try { return [long]$Value }
    catch { return $Default }
}

function Convert-SafeInt {
    param([object]$Value, [int]$Default = 0)
    if ($null -eq $Value) { return $Default }
    try { return [int]$Value }
    catch { return $Default }
}

function Convert-SafeDouble {
    param([object]$Value, [double]$Default = 0.0)
    if ($null -eq $Value) { return $Default }
    try { return [double]$Value }
    catch { return $Default }
}

function Get-CimOrWmi {
    <#
    .SYNOPSIS
        Fallback automatique CIM -> WMI (v6.6.2)
    #>
    param(
        [string]$ClassName,
        [string]$Namespace = 'root/cimv2',
        [string]$Filter = ''
    )
    $result = $null
    $source = 'none'
    
    # Methode 1: Get-CimInstance
    try {
        if ($Filter) {
            $result = Get-CimInstance -ClassName $ClassName -Namespace $Namespace -Filter $Filter -ErrorAction Stop
        } else {
            $result = Get-CimInstance -ClassName $ClassName -Namespace $Namespace -ErrorAction Stop
        }
        if ($null -ne $result) { $source = 'CIM' }
    } catch { }
    
    # Methode 2: Get-WmiObject (fallback)
    if ($null -eq $result) {
        try {
            if ($Filter) {
                $result = Get-WmiObject -Class $ClassName -Namespace $Namespace -Filter $Filter -ErrorAction Stop
            } else {
                $result = Get-WmiObject -Class $ClassName -Namespace $Namespace -ErrorAction Stop
            }
            if ($null -ne $result) { $source = 'WMI' }
        } catch { }
    }
    
    return [PSCustomObject]@{
        Data = $result
        Source = $source
    }
}

#
# Helper: exécute une commande avec timeout et capture stdout/stderr
#
function Invoke-CommandWithTimeout {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList = @(),
        [int]$TimeoutSeconds = 20
    )
    $result = [ordered]@{ success = $false; exitCode = -1; output = @(); error = '' }
    if ([string]::IsNullOrEmpty($FilePath)) { return $result }
    try {
        $tempOut = [System.IO.Path]::Combine($env:TEMP, "cmd_out_$([Guid]::NewGuid().ToString()).txt")
        $tempErr = [System.IO.Path]::Combine($env:TEMP, "cmd_err_$([Guid]::NewGuid().ToString()).txt")
        $proc = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -NoNewWindow -PassThru `
            -RedirectStandardOutput $tempOut -RedirectStandardError $tempErr
        if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
            try { $proc.Kill() } catch { }
            $result['error'] = "Timeout after $TimeoutSeconds seconds"
            return $result
        }
        $result['exitCode'] = $proc.ExitCode
        if (Test-Path $tempOut) { $result['output'] = @(Get-Content -Path $tempOut -ErrorAction SilentlyContinue) }
        if (Test-Path $tempErr) { $result['error'] = (Get-Content -Path $tempErr -ErrorAction SilentlyContinue) -join "`n" }
        $result['success'] = ($proc.ExitCode -eq 0)
        return $result
    } catch {
        $result['error'] = $_.Exception.Message
        return $result
    } finally {
        try { if (Test-Path $tempOut) { Remove-Item -Path $tempOut -Force -ErrorAction SilentlyContinue } } catch { }
        try { if (Test-Path $tempErr) { Remove-Item -Path $tempErr -Force -ErrorAction SilentlyContinue } } catch { }
    }
}

#
# Convertit récursivement un objet en structure sérialisable JSON (pas d'OrderedDictionary, clés string)
# - Les dictionnaires sont transformés en hashtable à clés string
# - Les listes/énumérables sont converties en tableaux de valeurs sûres
# - Les PSCustomObject et objets .NET avec propriétés sont convertis en hashtable de propriétés
# - Les chaînes et types primitifs sont retournés tels quels
# Ce helper garantit que ConvertTo-Json ne lève pas d'exception sous PowerShell 5.1.
function ConvertTo-JsonSafeObject {
    param([object]$InputObject)
    # Valeur nulle
    if ($null -eq $InputObject) { return $null }
    # Ne pas traiter les chaînes comme IEnumerable
    if ($InputObject -is [string] -or $InputObject -is [char] -or $InputObject.GetType().IsPrimitive) {
        return $InputObject
    }
    # Dictionnaires: hashtable à clés string
    if ($InputObject -is [System.Collections.IDictionary]) {
        $ht = @{}
        foreach ($k in $InputObject.Keys) {
            try {
                $strKey = ''
                if ($null -ne $k) { $strKey = [string]$k }
                $ht[$strKey] = ConvertTo-JsonSafeObject ($InputObject[$k])
            } catch { }
        }
        return $ht
    }
    # Collections (IEnumerable) autres que string
    if ($InputObject -is [System.Collections.IEnumerable] -and -not ($InputObject -is [string])) {
        $arr = @()
        foreach ($item in $InputObject) {
            $arr += ConvertTo-JsonSafeObject $item
        }
        return $arr
    }
    # PSCustomObject ou objets .NET: convertir propriétés en hashtable
    $props = $InputObject.PSObject.Properties
    if ((Get-SafeCount $props) -gt 0) {
        $objHt = @{}
        foreach ($prop in $props) {
            try {
                $propName = [string]$prop.Name
                $objHt[$propName] = ConvertTo-JsonSafeObject $prop.Value
            } catch { }
        }
        return $objHt
    }
    # Fallback: retourner sous forme de chaîne
    try { return [string]$InputObject } catch { return $InputObject }
}

function Test-TemperatureValue {
    param([double]$Value, [double]$Min, [double]$Max)
    try {
        if ($null -eq $Value) { return $false }
        if ($Value -lt $Min -or $Value -gt $Max) { return $false }
        return $true
    } catch {
        return $false
    }
}

function Normalize-Temperature {
    <#
    .SYNOPSIS
        Normalise une temperature avec conversion Kelvin automatique (v6.6.3)
    #>
    param(
        [object]$Value,
        [double]$Min = 0,
        [double]$Max = 95
    )
    try {
        if ($null -eq $Value) { return $null }
        $temp = $null
        try { $temp = [double]$Value } catch { return $null }
        if ($null -eq $temp) { return $null }
        
        # Conversion Kelvin -> Celsius AVANT validation
        if ($temp -gt 200 -and $temp -lt 500) {
            # Kelvin standard (ex: 300K = 27C)
            $temp = $temp - 273.15
        }
        elseif ($temp -gt 1000 -and $temp -lt 5000) {
            # Dixiemes de Kelvin (ex: 3000 = 300K = 27C)
            $temp = ($temp / 10) - 273.15
        }
        
        # Validation apres conversion
        if ($temp -lt $Min -or $temp -gt $Max) { return $null }
        return [math]::Round($temp, 1)
    } catch {
        return $null
    }
}

function Extract-SmartTemperature {
    <#
    .SYNOPSIS
        Extrait temperature SMART depuis raw value (v6.6.6)
        Lit le LOW BYTE, pas l'entier complet
        Plage valide: 0-125 degres Celsius
    #>
    param([object]$RawValue)
    try {
        if ($null -eq $RawValue) { return $null }
        $raw = [long]$RawValue
        
        # Low byte = temperature en Celsius (methode standard SMART)
        $lowByte = $raw -band 0xFF
        if ($lowByte -ge 0 -and $lowByte -le 125) {
            return [int]$lowByte
        }
        
        # Essai byte suivant si low byte hors plage
        $nextByte = ($raw -shr 8) -band 0xFF
        if ($nextByte -ge 0 -and $nextByte -le 125) {
            return [int]$nextByte
        }
        
        # Fallback: valeur directe si dans plage valide
        if ($raw -ge 0 -and $raw -le 125) {
            return [int]$raw
        }
        
        # Valeur hors plage = invalide
        return $null
    } catch {
        return $null
    }
}

function Get-SectionStatus {
    param([string]$SectionKey, [object]$SectionData)
    try {
        $collectorName = Get-SafeDictValue $Script:SectionNameMap $SectionKey ''
        $statusEntry = $null
        if ($collectorName -and (Test-SafeHasKey $Script:CollectorStatus $collectorName)) {
            $statusEntry = $Script:CollectorStatus[$collectorName]
        } elseif (Test-SafeHasKey $Script:CollectorStatus $SectionKey) {
            $statusEntry = $Script:CollectorStatus[$SectionKey]
        }
        $rawStatus = ''
        if ($null -ne $statusEntry) { $rawStatus = Get-SafeDictValue $statusEntry 'status' '' }
        switch ($rawStatus) {
            'ok' { return 'OK' }
            'partial' { return 'PARTIAL' }
            'failed' { return 'FAILED' }
        }
        if ($null -ne $SectionData) { return 'OK' }
        return 'FAILED'
    } catch {
        return 'FAILED'
    }
}

function Get-JsonErrors {
    $errors = @()
    try {
        foreach ($err in @($Script:ErrorLog)) {
            if ($null -eq $err) { continue }
            $errors += [ordered]@{
                code = [string](Get-SafePropValue $err 'Type' 'UNKNOWN')
                message = [string](Get-SafePropValue $err 'Message' 'Unknown error')
                section = (Get-SafePropValue $err 'Source' $null)
                exceptionType = (Get-SafePropValue $err 'ExceptionType' $null)
            }
        }
    } catch { }
    return $errors
}

function Build-JsonSnapshot {
    $now = Get-Date
    $duration = $null
    try {
        $endTime = $now
        if ($null -ne $Script:EndTime) { $endTime = $Script:EndTime }
        $duration = [math]::Round(($endTime - $Script:StartTime).TotalSeconds, 2)
    } catch { $duration = 0.0 }

    $sections = [ordered]@{}
    foreach ($key in @($Script:SectionKeys)) {
        $sectionData = $null
        if ($null -ne $Script:SectionData -and (Test-SafeHasKey $Script:SectionData $key)) {
            $sectionData = $Script:SectionData[$key]
        }
        $sections[$key] = [ordered]@{
            status = (Get-SectionStatus -SectionKey $key -SectionData $sectionData)
            data = $sectionData
            summary = $null
        }
    }
    
    # Calculer redactLevel (PS 5.1 compatible)
    $redactLevelValue = $Script:RedactLevel
    if ($Script:NoRedact) { $redactLevelValue = 'NONE' }
    
    # Calculer ScoreV2
    $scoreV2Data = $null
    try { $scoreV2Data = Calculate-ScoreV2 } catch { $scoreV2Data = $null }
    
    # Collecter missingData
    $missingDataArray = @()
    try { $missingDataArray = @($Script:MissingData) } catch { $missingDataArray = @() }

    return [ordered]@{
        metadata = [ordered]@{
            version = [string]$Script:ScriptVersion
            runId = [string]$Script:RunId
            timestamp = $now.ToString('o')
            isAdmin = [bool]$Script:IsAdmin
            redactLevel = [string]$redactLevelValue
            quickScan = [bool]$Script:QuickScan
            monitorSeconds = [int]$Script:MonitorSeconds
            durationSeconds = [double]$duration
            partialFailure = [bool]$Script:PartialFailure
        }
        paths = [ordered]@{
            txtPath = [string]$Script:OutputPath
            jsonPath = [string]$Script:JsonOutputPath
            outputDir = [string]$Script:OutputDir
        }
        sections = $sections
        errors = @(Get-JsonErrors)
        findings = @($Script:Findings)
        missingData = $missingDataArray
        scoreV2 = $scoreV2Data
    }
}

function Build-MinimalReportLines {
    $lines = @()
    $lines += ("#" * 100)
    $lines += "# RAPPORT MINIMAL VIRTUAL IT PRO - COLLECTE PARTIELLE"
    $lines += ("#" * 100)
    $lines += "Run ID               : $($Script:RunId)"
    $lines += "Version              : $($Script:ScriptVersion)"
    $lines += "Date                 : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    $lines += "PartialFailure       : $($Script:PartialFailure)"
    $lines += "Sortie TXT           : $($Script:OutputPath)"
    $lines += "Sortie JSON          : $($Script:JsonOutputPath)"
    $lines += ""
    $lines += "SECTIONS COLLECTEES"
    $lines += ("-" * 50)
    $sectionKeys = @()
    if ($null -ne $Script:SectionData -and $Script:SectionData -is [System.Collections.IDictionary]) { $sectionKeys = @($Script:SectionData.Keys) }
    $lines += "Sections: $(Get-SafeCount $sectionKeys)"
    foreach ($key in $sectionKeys) { $lines += "  - $key" }
    $lines += ""
    $lines += "ERREURS"
    $lines += ("-" * 50)
    $lines += "Erreurs de collecte: $(Get-SafeCount $Script:ErrorLog)"
    foreach ($err in @($Script:ErrorLog)) {
        if ($null -eq $err) { continue }
        $lines += "[$(Get-SafePropValue $err 'Type' 'UNKNOWN')] $(Get-SafePropValue $err 'Source' 'Unknown') - $(Get-SafePropValue $err 'Message' 'No message')"
    }
    return $lines
}

#
# Helper: retourne le temps CPU en secondes pour un processus, en gérant tous les types sans lever d'exception
#
function Get-ProcCpuSeconds {
    param([object]$Proc)
    try {
        if ($null -eq $Proc) { return 0.0 }
        # Obtenir la valeur CPU de façon sûre
        $cpuVal = Get-SafePropValue $Proc 'CPU'
        # Si la propriété CPU est renseignée, tenter de calculer les secondes CPU
        if ($null -ne $cpuVal) {
            # Si c'est un TimeSpan, utiliser TotalSeconds
            if ($cpuVal -is [System.TimeSpan]) {
                return [double]$cpuVal.TotalSeconds
            }
            # Si c'est un nombre (double/int), tenter une conversion directe
            try { return [double]$cpuVal } catch { }
            # Si la propriété TotalSeconds existe, l'utiliser (ex: TimeSpan masqué)
            $matchCount = 0
            if ($cpuVal.PSObject) { $matchCount = Get-SafeCount ($cpuVal.PSObject.Properties.Match('TotalSeconds')) }
            if ($matchCount -gt 0) {
                try { return [double]$cpuVal.TotalSeconds } catch { }
            }
        }
        # Fallback : utiliser TotalProcessorTime si disponible (TimeSpan)
        $tpt = Get-SafePropValue $Proc 'TotalProcessorTime'
        if ($tpt -is [System.TimeSpan]) {
            return [double]$tpt.TotalSeconds
        }
        return 0.0
    } catch {
        return 0.0
    }
}

#
# Helper: validate a VRAM value based on GPU name and memory constraints
# Retourne $true si la valeur est jugée fiable, $false sinon
function Test-VramValue {
    param(
        [string]$gpuName,
        [int]$vramMB
    )
    try {
        # Si VRAM est absent ou inférieure à 512 MB, invalide
        if ($null -eq $vramMB -or $vramMB -lt 512) { return $false }
        # Doit être un multiple de 256 MB
        if (($vramMB % 256) -ne 0) { return $false }
        # Rejeter certaines valeurs erronées fréquentes
        if ($vramMB -in @(4095, 2047, 1023)) { return $false }
        $lower = ''
        if ($null -ne $gpuName) { $lower = $gpuName.ToLower() }
        # Cartes haut de gamme : minimum 8 GB (8192 MB)
        if ($lower -match '3090' -and $vramMB -lt 8192) { return $false }
        if ($lower -match '3080' -and $vramMB -lt 8192) { return $false }
        return $true
    } catch {
        return $false
    }
}

#
# Helper: récupère les informations GPU via nvidia-smi si disponible
# Retourne une liste d'objets { index, name, vramMB, driver }
function Get-NvidiaGpuInfo {
    try {
        $result = @()
        # Rechercher la commande nvidia-smi
        $cmd = Get-Command 'nvidia-smi.exe' -ErrorAction SilentlyContinue
        if ($null -ne $cmd) {
            $args = @('--query-gpu=index,name,memory.total,driver_version', '--format=csv,noheader,nounits')
            $exec = Invoke-CommandWithTimeout -FilePath $cmd.Source -ArgumentList $args -TimeoutSeconds $Script:ExternalCommandTimeoutSeconds
            if ($exec['success'] -and $null -ne $exec['output']) {
                $lines = @($exec['output'])
                foreach ($l in $lines) {
                    $parts = $l.Split(',') | ForEach-Object { $_.Trim() }
                    if ((Get-SafeCount $parts) -ge 3) {
                        $idx = $parts[0]
                        $gpuName = $parts[1]
                        $memStr = $parts[2]
                        $drv = ''
                        if ((Get-SafeCount $parts) -ge 4) { $drv = $parts[3] }
                        $mb = 0
                        try { $mb = [int]$memStr } catch { try { $mb = [int]([double]$memStr) } catch { $mb = 0 } }
                        $result += [ordered]@{
                            index = Convert-SafeInt $idx
                            name = $gpuName
                            vramMB = $mb
                            driver = $drv
                        }
                    }
                }
            } elseif ($exec['error']) {
                Add-ErrorLog -Type 'GPU_WARN' -Source 'Get-NvidiaGpuInfo' -Message $exec['error']
            }
        }
        return $result
    } catch {
        return @()
    }
}

#
# Helper: récupère les informations GPU via dxdiag
# Utilise l'export texte de dxdiag (/t) et analyse les lignes pour obtenir le nom et la mémoire dédiée
function Get-DxdiagGpuInfo {
    try {
        $list = @()
        $tempFile = [System.IO.Path]::Combine($env:TEMP, "dxdiag_gpu_$([Guid]::NewGuid().ToString()).txt")
        # Exécuter dxdiag en mode silencieux vers fichier texte
        $exec = Invoke-CommandWithTimeout -FilePath 'dxdiag.exe' -ArgumentList @('/t', $tempFile) -TimeoutSeconds $Script:ExternalCommandTimeoutSeconds
        if (Test-Path $tempFile) {
            $lines = Get-Content -Path $tempFile -ErrorAction SilentlyContinue
            Remove-Item -Path $tempFile -Force -ErrorAction SilentlyContinue
            $currentName = ''
            foreach ($ln in $lines) {
                $trim = ($ln -as [string]).Trim()
                # Trouver la ligne du nom de carte
                if ($trim -match '^Card name\s*:\s*(.+)$') {
                    $currentName = $Matches[1].Trim()
                    continue
                }
                # Trouver la mémoire dédiée (Dedicated Memory ou Dedicated Video Memory)
                if ($trim -match '^Dedicated (Video )?Memory\s*:\s*(\d+(?:\.\d+)?)\s*(GB|MB)$') {
                    $valStr = $Matches[2]
                    $unit = $Matches[3]
                    $val = 0.0
                    try { $val = [double]$valStr } catch { $val = 0.0 }
                    $memMB = 0
                    if ($unit -match 'GB') {
                        $memMB = [int]([math]::Round($val * 1024))
                    } else {
                        $memMB = [int]([math]::Round($val))
                    }
                    if ($currentName) { $list += [ordered]@{ name = $currentName; vramMB = $memMB } }
                }
            }
        }
        if (-not $exec['success'] -and $exec['error']) {
            Add-ErrorLog -Type 'GPU_WARN' -Source 'Get-DxdiagGpuInfo' -Message $exec['error']
        }
        return $list
    } catch {
        return @()
    }
}

function Get-RegistryGpuInfo {
    try {
        $list = @()
        $classKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
        $keys = Get-ChildItem -Path $classKey -ErrorAction SilentlyContinue
        foreach ($key in @($keys)) {
            if ($null -eq $key) { continue }
            $psPath = Get-SafePropValue $key 'PSPath' ''
            if ([string]::IsNullOrEmpty($psPath)) { continue }
            $props = Get-ItemProperty -Path $psPath -ErrorAction SilentlyContinue
            if ($null -eq $props) { continue }
            $memBytes = Get-SafePropValue $props 'HardwareInformation.qwMemorySize' $null
            $memMB = 0
            if ($null -ne $memBytes) { $memMB = [math]::Round((Convert-SafeLong $memBytes) / 1MB, 0) }
            if ($memMB -le 0) { continue }
            $list += [ordered]@{
                name = (Get-SafePropValue $props 'DriverDesc' '')
                matchingId = (Get-SafePropValue $props 'MatchingDeviceId' '')
                pnpDeviceId = (Get-SafePropValue $props 'PnPDeviceID' '')
                vramMB = $memMB
            }
        }
        return $list
    } catch {
        return @()
    }
}

function Get-GpuVendor {
    param([string]$Name, [string]$PnpDeviceId)
    $combined = ("$Name $PnpDeviceId").ToLower()
    if ($combined -match 'nvidia') { return 'NVIDIA' }
    if ($combined -match 'amd|radeon|advanced micro devices') { return 'AMD' }
    if ($combined -match 'intel') { return 'Intel' }
    return 'Unknown'
}

# Helper: charge la DLL LibreHardwareMonitorLib.dll si elle est présente (avec téléchargement optionnel) et retourne $true/$false
# Constante pour le caractere degre (evite les problemes d'encodage UTF-8)
$Script:DegreeSymbol = [char]176

function Load-LibreHardwareMonitor {
    <#
    .SYNOPSIS
        Charge la DLL LibreHardwareMonitor si disponible localement (v6.6.5)
    .DESCRIPTION
        Recherche dans plusieurs emplacements: app packagée, script, Program Files.
        LICENCE: LibreHardwareMonitor est sous MPL 2.0 - à vérifier par l'équipe.
    #>
    try {
        # Verifier si deja charge en memoire
        $lhmType = [System.AppDomain]::CurrentDomain.GetAssemblies() | 
            Where-Object { $_.FullName -like '*LibreHardwareMonitor*' }
        if ($null -ne $lhmType) { return $true }
        
        # Chemins possibles (app packagée prioritaire)
        $possiblePaths = @(
            (Join-Path $PSScriptRoot 'LibreHardwareMonitorLib.dll'),
            (Join-Path $PSScriptRoot 'Sensors\LibreHardwareMonitorLib.dll'),
            (Join-Path $PSScriptRoot 'lib\LibreHardwareMonitorLib.dll'),
            (Join-Path $PSScriptRoot '..\Bin\LibreHardwareMonitorLib.dll'),
            'C:\Virtual IT Pro\Bin\LibreHardwareMonitorLib.dll',
            'C:\Virtual IT Pro\Sensors\LibreHardwareMonitorLib.dll',
            (Join-Path $env:ProgramFiles 'Virtual IT Pro\Bin\LibreHardwareMonitorLib.dll'),
            (Join-Path $env:ProgramFiles 'LibreHardwareMonitor\LibreHardwareMonitorLib.dll'),
            (Join-Path ${env:ProgramFiles(x86)} 'LibreHardwareMonitor\LibreHardwareMonitorLib.dll')
        )
        
        $dllPath = $null
        foreach ($path in $possiblePaths) {
            if ($path -and (Test-Path $path -ErrorAction SilentlyContinue)) { 
                $dllPath = $path
                break 
            }
        }
        
        if ($null -eq $dllPath) {
            Add-ErrorLog -Type 'TEMP_INFO' -Source 'Load-LibreHardwareMonitor' -Message 'DLL non trouvee - fallback WMI actif'
            return $false
        }
        
        Add-Type -Path $dllPath -ErrorAction Stop
        return $true
    }
    catch {
        Add-ErrorLog -Type 'TEMP_WARN' -Source 'Load-LibreHardwareMonitor' -Message "Echec chargement: $($_.Exception.Message)"
        return $false
    }
}

function Get-NvidiaSmiTemperature {
    <#
    .SYNOPSIS
        Recupere la temperature GPU via nvidia-smi (tres fiable pour NVIDIA)
    .OUTPUTS
        Hashtable avec Value et Source, ou $null si echec
    #>
    try {
        # nvidia-smi est installe avec les drivers NVIDIA
        $nvidiaSmiPaths = @(
            'C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe',
            'C:\Windows\System32\nvidia-smi.exe',
            (Get-Command 'nvidia-smi.exe' -ErrorAction SilentlyContinue).Source
        )
        
        $nvidiaSmi = $null
        foreach ($path in $nvidiaSmiPaths) {
            if ($path -and (Test-Path $path -ErrorAction SilentlyContinue)) { 
                $nvidiaSmi = $path
                break 
            }
        }
        
        if ($null -eq $nvidiaSmi) { return $null }
        
        # Executer nvidia-smi pour obtenir la temperature
        $output = & $nvidiaSmi --query-gpu=temperature.gpu --format=csv,noheader,nounits 2>$null
        
        if ($null -ne $output -and $output -match '^\d+$') {
            $temp = [double]$output.Trim()
            if ($temp -gt 0 -and $temp -lt 150) {
                return @{
                    Value = [math]::Round($temp, 1)
                    Source = 'nvidia-smi'
                }
            }
        }
        return $null
    }
    catch { return $null }
}

function Get-WmiCpuTemperature {
    <#
    .SYNOPSIS
        Recupere la temperature CPU via WMI (MSAcpi_ThermalZoneTemperature)
    .DESCRIPTION
        Utilise la VRAIE classe WMI dans root\WMI (necessite admin)
    #>
    try {
        # Methode 1: MSAcpi_ThermalZoneTemperature (la plus fiable)
        $thermalZones = Get-CimInstance -Namespace 'root\WMI' -ClassName 'MSAcpi_ThermalZoneTemperature' -ErrorAction Stop
        
        if ($null -ne $thermalZones) {
            $maxTemp = $null
            foreach ($zone in @($thermalZones)) {
                if ($null -eq $zone) { continue }
                # CurrentTemperature est en dixiemes de Kelvin
                $currentTemp = Get-SafePropValue $zone 'CurrentTemperature' 0
                if ($currentTemp -gt 0) {
                    # Conversion: (valeur / 10) - 273.15 = Celsius
                    $celsius = ($currentTemp / 10) - 273.15
                    if ($celsius -gt -50 -and $celsius -lt 150) {
                        if ($null -eq $maxTemp -or $celsius -gt $maxTemp) {
                            $maxTemp = $celsius
                        }
                    }
                }
            }
            if ($null -ne $maxTemp) {
                return @{ Value = [math]::Round($maxTemp, 1); Source = 'WMI (MSAcpi)' }
            }
        }
    }
    catch { }
    
    # Methode 2: ThermalZoneInformation (Windows 10+)
    try {
        $perfData = Get-CimInstance -ClassName 'Win32_PerfFormattedData_Counters_ThermalZoneInformation' -ErrorAction Stop
        if ($null -ne $perfData) {
            foreach ($zone in @($perfData)) {
                if ($null -eq $zone) { continue }
                $highPrecTemp = Get-SafePropValue $zone 'HighPrecisionTemperature' 0
                if ($highPrecTemp -gt 0) {
                    $celsius = ($highPrecTemp / 10) - 273.15
                    if ($celsius -gt -50 -and $celsius -lt 150) {
                        return @{ Value = [math]::Round($celsius, 1); Source = 'WMI (ThermalZone)' }
                    }
                }
            }
        }
    }
    catch { }
    
    return $null
}

function Get-GpuTempFallback {
    <#
    .SYNOPSIS
        Fallback pour temperature GPU (apres nvidia-smi et LHM)
    .DESCRIPTION
        Note: Win32_VideoController n'a PAS de propriete Temperature standard.
        Cette fonction est un placeholder pour futures implementations.
    #>
    try {
        # Win32_VideoController n'expose PAS Temperature - c'est un mythe
        # Retourner null pour forcer l'affichage "Non detecte"
        return $null
    }
    catch { return $null }
}

function Get-SmartTemperatureFromVendorSpecific {
    <#
    .SYNOPSIS
        Extrait temperature SMART depuis VendorSpecific (v6.6.3)
        Attributs 194 (Temperature_Celsius) ou 190 (Airflow_Temperature)
        Lecture du LOW BYTE de la raw value
    #>
    param([byte[]]$VendorSpecific)
    try {
        if ($null -eq $VendorSpecific) { return $null }
        $rawData = @($VendorSpecific)
        $rawLen = Get-SafeCount $rawData
        
        for ($i = 2; $i -lt ($rawLen - 12); $i += 12) {
            $attrId = $rawData[$i]
            # Attribut 194 (Temperature_Celsius) ou 190 (Airflow_Temperature)
            if ($attrId -eq 194 -or $attrId -eq 190) {
                # Raw value commence a offset i+5, low byte = temperature
                $lowByte = $rawData[$i + 5]
                $result = Extract-SmartTemperature -RawValue $lowByte
                if ($null -ne $result) { return $result }
                
                # Fallback: essayer lecture entiere si low byte echoue
                try { 
                    $fullValue = [BitConverter]::ToUInt32($rawData, $i + 5)
                    $result = Extract-SmartTemperature -RawValue $fullValue
                    if ($null -ne $result) { return $result }
                } catch { }
            }
        }
        return $null
    } catch {
        return $null
    }
}

function Get-DiskTemperatures {
    $disks = @()
    try {
        $cmd = Get-Command 'Get-StorageReliabilityCounter' -ErrorAction SilentlyContinue
        if ($null -ne $cmd) {
            $reliability = Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
            foreach ($item in @($reliability)) {
                if ($null -eq $item) { continue }
                $tempRaw = Get-SafePropValue $item 'Temperature' $null
                $temp = Normalize-Temperature -Value $tempRaw -Min 0 -Max 90
                if ($null -eq $temp -and $null -ne $tempRaw) {
                    Add-ErrorLog -Type 'TEMP_WARN' -Source 'DiskTemperature' -Message "Lecture disque invalide (StorageReliabilityCounter): $tempRaw"
                }
                $disks += [ordered]@{
                    model = Get-SafePropValue $item 'FriendlyName' $null
                    serial = Protect-SerialNumber (Get-SafePropValue $item 'SerialNumber' $null)
                    tempC = $temp
                    source = 'StorageReliabilityCounter'
                }
            }
            if ((Get-SafeCount $disks) -gt 0) { return $disks }
        }
    } catch { }

    try {
        $diskList = @((Get-WmiSafe -ClassName 'Win32_DiskDrive'))
        $smartList = @((Get-WmiSafe -Namespace 'root\\WMI' -ClassName 'MSStorageDriver_ATAPISmartData'))
        $diskCount = Get-SafeCount $diskList
        $smartCount = Get-SafeCount $smartList
        for ($i = 0; $i -lt $diskCount; $i++) {
            $disk = $diskList[$i]
            if ($null -eq $disk) { continue }
            $smartItem = $null
            if ($i -lt $smartCount) { $smartItem = $smartList[$i] }
            $tempRaw = $null
            if ($null -ne $smartItem) {
                $vendorSpecific = Get-SafePropValue $smartItem 'VendorSpecific' $null
                $tempRaw = Get-SmartTemperatureFromVendorSpecific -VendorSpecific $vendorSpecific
            }
            $temp = Normalize-Temperature -Value $tempRaw -Min 0 -Max 90
            if ($null -eq $temp -and $null -ne $tempRaw) {
                Add-ErrorLog -Type 'TEMP_WARN' -Source 'DiskTemperature' -Message "Lecture SMART invalide: $tempRaw"
            }
            $disks += [ordered]@{
                model = Get-SafePropValue $disk 'Model' $null
                serial = Protect-SerialNumber (Get-SafePropValue $disk 'SerialNumber' $null)
                tempC = $temp
                source = 'SMART'
            }
        }
    } catch { }
    return $disks
}

#endregion

#region ============== FONCTIONS UTILITAIRES ==============
function Test-Administrator {
    try {
        $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($currentUser)
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    catch { return $false }
}

function Show-Progress {
    param([int]$current, [int]$total, [string]$section)
    try {
        if ($total -le 0) { return }
        $percent = [math]::Floor(($current * 100) / $total)
        $width = 40
        $filled = [math]::Floor(($percent * $width) / 100)
        $bar = '[' + ('#' * $filled) + (' ' * ($width - $filled)) + ']'
        Write-Host "`r$section | $bar $percent%".PadRight(100) -NoNewline -ForegroundColor Green
    }
    catch { }
}

function Get-ShortHash {
    param([string]$Value)
    try {
        if ([string]::IsNullOrEmpty($Value)) { return "000000" }
        if (Test-SafeHasKey $Script:RedactCache $Value) { return $Script:RedactCache[$Value] }
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        $hashBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value))
        $sha256.Dispose()
        $shortHash = [BitConverter]::ToString($hashBytes).Replace('-', '').Substring(0, 6).ToLower()
        $Script:RedactCache[$Value] = $shortHash
        return $shortHash
    }
    catch { return "000000" }
}

function Protect-Username {
    param([string]$Username)
    if ([string]::IsNullOrEmpty($Username) -or $Script:NoRedact) { return $Username }
    try { $Script:RedactionStats.TotalRedactions++; return "USER-$(Get-ShortHash $Username)" }
    catch { return $Username }
}

function Protect-ProductKey {
    param([string]$Key)
    if ([string]::IsNullOrEmpty($Key) -or $Script:NoRedact) { return $Key }
    try {
        $Script:RedactionStats.TotalRedactions++
        if ($Script:RedactLevel -eq 'Full') { return "KEY-XXXX" }
        if ($Key.Length -gt 4) { return "KEY-***$($Key.Substring($Key.Length - 4))" }
        return "KEY-XXXX"
    }
    catch { return "KEY-XXXX" }
}

function Protect-IPAddress {
    param([string]$IP)
    if ([string]::IsNullOrEmpty($IP) -or $Script:NoRedact) { return $IP }
    try {
        if ($IP -match '^192\.168\.') { $Script:RedactionStats.TotalRedactions++; return '192.168.x.x' }
        if ($IP -match '^10\.') { $Script:RedactionStats.TotalRedactions++; return '10.x.x.x' }
        if ($IP -match '^172\.(1[6-9]|2[0-9]|3[0-1])\.') { $Script:RedactionStats.TotalRedactions++; return '172.x.x.x' }
        if ($IP -match '^127\.') { $Script:RedactionStats.TotalRedactions++; return '127.x.x.x' }
        if ($IP -match '^fe80:') { $Script:RedactionStats.TotalRedactions++; return 'fe80::xxxx' }
        if ($Script:RedactLevel -eq 'Full') { $Script:RedactionStats.TotalRedactions++; return "IP-$(Get-ShortHash $IP)" }
        if ($IP -match '^[0-9]{1,3}(\.[0-9]{1,3}){3}$') { $Script:RedactionStats.TotalRedactions++; return 'x.x.x.x' }
        if ($IP -match '^[0-9a-fA-F:]+$') { $Script:RedactionStats.TotalRedactions++; return 'xxxx:xxxx:xxxx:xxxx' }
        return $IP
    }
    catch { return $IP }
}

function Protect-SerialNumber {
    param([string]$Serial)
    if ([string]::IsNullOrEmpty($Serial) -or $Script:NoRedact) { return $Serial }
    try {
        $Script:RedactionStats.TotalRedactions++
        if ($Script:RedactLevel -eq 'Full') { return "SERIAL-XXXX" }
        if ($Serial.Length -gt 4) { return "SERIAL-***$($Serial.Substring($Serial.Length - 4))" }
        return "SERIAL-XXXX"
    }
    catch { return "SERIAL-XXXX" }
}

function Protect-PnpDeviceId {
    param([string]$PnpDeviceId)
    if ([string]::IsNullOrEmpty($PnpDeviceId) -or $Script:NoRedact) { return $PnpDeviceId }
    try {
        $Script:RedactionStats.TotalRedactions++
        if ($Script:RedactLevel -eq 'Full') { return "PNP-XXXX" }
        return "PNP-$(Get-ShortHash $PnpDeviceId)"
    }
    catch { return $PnpDeviceId }
}

function Protect-ProfilePath {
    param([string]$Path)
    if ([string]::IsNullOrEmpty($Path) -or $Script:NoRedact) { return $Path }
    try {
        if ($Path -match '^[A-Za-z]:\\Users\\([^\\]+)(.*)$') {
            $matchCount = Get-SafeCount $Matches
            $username = ''
            if ($Matches -and $matchCount -gt 1) { $username = $Matches[1] }
            $remainder = ''
            if ($Matches -and $matchCount -gt 2) { $remainder = $Matches[2] }
            if ($username -in @('Public', 'Default', 'Default User', 'All Users')) { return $Path }
            $Script:RedactionStats.TotalRedactions++
            return "%USERPROFILE%$remainder"
        }
        return $Path
    }
    catch { return $Path }
}

function Protect-EnvValue {
    param([string]$Key, [string]$Value)
    if ([string]::IsNullOrEmpty($Value) -or $Script:NoRedact) { return $Value }
    try {
        if ($Script:RedactLevel -eq 'Full') { $Script:RedactionStats.TotalRedactions++; return "ENV-$(Get-ShortHash $Value)" }
        if ($Key -match '(?i)(password|secret|token|key|pwd)') { $Script:RedactionStats.TotalRedactions++; return "REDACTED" }
        if ($Value -match '^[A-Za-z]:\\Users\\') { $Script:RedactionStats.TotalRedactions++; return (Protect-ProfilePath $Value) }
        return $Value
    } catch { return $Value }
}

function Protect-ComputerName {
    param([string]$ComputerName)
    if ([string]::IsNullOrEmpty($ComputerName) -or $Script:NoRedact) { return $ComputerName }
    try {
        if ($Script:RedactLevel -eq 'Full') {
            $Script:RedactionStats.TotalRedactions++
            return "PC-$(Get-ShortHash $ComputerName)"
        }
        return $ComputerName
    }
    catch { return $ComputerName }
}

function Protect-MACAddress {
    param([string]$MAC)
    if ([string]::IsNullOrEmpty($MAC) -or $Script:NoRedact) { return $MAC }
    try {
        if ($Script:RedactLevel -eq 'Full') {
            $Script:RedactionStats.TotalRedactions++
            if ($MAC -match '^([0-9A-Fa-f]{2}[:-]){2}[0-9A-Fa-f]{2}') { return $MAC.Substring(0, 8) + ':XX:XX:XX' }
            return 'XX:XX:XX:XX:XX:XX'
        }
        return $MAC
    }
    catch { return $MAC }
}

function Protect-CertificateSubject {
    param([string]$Subject)
    if ([string]::IsNullOrEmpty($Subject) -or $Script:NoRedact) { return $Subject }
    try {
        if ($Script:RedactLevel -eq 'Full') { $Script:RedactionStats.TotalRedactions++; return "CERT-$(Get-ShortHash $Subject)" }
        return $Subject
    }
    catch { return $Subject }
}

function Get-WmiSafe {
    param([string]$ClassName, [string]$Namespace = 'root\cimv2', [string]$Filter = '', [switch]$BypassCache)
    try {
        $cacheKey = "$Namespace\$ClassName\$Filter"
        if (-not $BypassCache -and (Test-SafeHasKey $Script:WmiCache $cacheKey)) { 
            return $Script:WmiCache[$cacheKey] 
        }
        $params = @{ ClassName = $ClassName; Namespace = $Namespace; ErrorAction = 'Stop' }
        if ($Filter) { $params['Filter'] = $Filter }
        $result = Get-CimInstance @params
        $Script:WmiCache[$cacheKey] = $result
        return $result
    }
    catch {
        $errorEntry = [PSCustomObject]@{ 
            Timestamp = (Get-Date).ToString('o'); Type = 'WMI_ERROR'
            Namespace = $Namespace; ClassName = $ClassName; Reason = $_.Exception.Message 
        }
        try { [void]$Script:ErrorLog.Add($errorEntry) } catch { }
        return $null
    }
}

function Add-ErrorLog {
    param([string]$Type, [string]$Source, [string]$Message)
    try {
        $errorEntry = [PSCustomObject]@{ 
            Timestamp = (Get-Date).ToString('o'); Type = $Type
            Source = $Source; Message = $Message 
        }
        [void]$Script:ErrorLog.Add($errorEntry)
        if ($Type -and ($Type -notmatch 'INFO')) { $Script:PartialFailure = $true }
    }
    catch { }
}

function Add-WmiError {
    <#
    .SYNOPSIS
        Journalise erreur WMI/CIM avec details complets (v6.6.3)
    #>
    param(
        [string]$Namespace,
        [string]$ClassName,
        [string]$Method,
        [object]$Exception
    )
    $msg = "[$Method] $Namespace\$ClassName"
    if ($null -ne $Exception) {
        $exMsg = $Exception.Message
        if ([string]::IsNullOrEmpty($exMsg)) { $exMsg = $Exception.ToString() }
        if ([string]::IsNullOrEmpty($exMsg)) { $exMsg = 'Exception sans message' }
        $msg += " - $exMsg"
    } else {
        $msg += " - Echec sans exception"
    }
    Add-ErrorLog -Type 'WMI_ERROR' -Source $ClassName -Message $msg
}

function Test-ValidValue {
    <#
    .SYNOPSIS
        Distingue valeur valide (incluant 0) de null/empty (v6.6.3)
    #>
    param([object]$Value, [switch]$AllowZero)
    if ($null -eq $Value) { return $false }
    if ($Value -is [string] -and [string]::IsNullOrEmpty($Value)) { return $false }
    if ($Value -is [array] -and $Value.Count -eq 0) { return $false }
    # 0 est valide par defaut
    return $true
}

function Invoke-WithFallback {
    <#
    .SYNOPSIS
        Execute une liste de methodes avec fallback automatique (v6.6.2)
    #>
    param(
        [scriptblock[]]$Methods,
        [string]$Section,
        [string]$Item,
        [object]$Default = $null
    )
    $result = $null
    $sourceUsed = 'none'
    $quality = 'unavailable'
    $reason = ''
    
    for ($i = 0; $i -lt $Methods.Count; $i++) {
        try {
            $result = & $Methods[$i]
            if ($null -ne $result -and $result -ne '' -and $result -ne 0) {
                $sourceUsed = "method_$i"
                $quality = 'ok'
                if ($i -gt 0) { $quality = 'fallback' }
                break
            }
        } catch {
            $reason = $_.Exception.Message
        }
    }
    
    if ($null -eq $result -or $result -eq '') {
        $result = $Default
        $quality = 'unavailable'
        Add-MissingData -Section $Section -Item $Item -Reason $reason
    }
    
    return [PSCustomObject]@{
        Value = $result
        Source = $sourceUsed
        Quality = $quality
        Reason = $reason
    }
}

function Add-MissingData {
    <#
    .SYNOPSIS
        Ajoute une entree de donnee manquante au tableau global missingData[]
    #>
    param(
        [string]$Section,
        [string]$Item,
        [string]$Reason
    )
    try {
        $entry = [PSCustomObject]@{
            section = $Section
            item = $Item
            reason = $Reason
            timestamp = (Get-Date).ToString('o')
        }
        [void]$Script:MissingData.Add($entry)
    } catch { }
}

function Calculate-ScoreV2 {
    <#
    .SYNOPSIS
        Calcule le ScoreV2 avec breakdown detaille (v7.0)
    .DESCRIPTION
        Base: 100
        Penalites ponderees (problemes REELS uniquement):
        - CRITICAL/FATAL: -25 (max 2 comptés = -50 cap)
        - COLLECTOR_ERROR: -3 par section (sauf limitations techniques)
        - WARN: -1 (max 10 comptés)
        - TIMEOUT: -5
        v7.0: Les limitations techniques (WMI, externalisees) ne penalisent PAS le score
        Score minimum: 10 (jamais 0 sauf crash total)
    #>
    $baseScore = 100
    $penalties = [ordered]@{
        critical = 0
        collectorErrors = 0
        warnings = 0
        timeouts = 0
        infoIssues = 0
        excludedLimitations = 0
    }
    $topPenalties = @()
    
    # Compter les erreurs par type
    foreach ($err in @($Script:ErrorLog)) {
        if ($null -eq $err) { continue }
        $errType = Get-SafePropValue $err 'Type' ''
        $errSource = Get-SafePropValue $err 'Source' 'Unknown'
        $errMsg = Get-SafePropValue $err 'Message' ''
        
        # v7.0: Exclure les limitations techniques du score
        $isLimitation = $false
        if ($errMsg -match 'limitation WMI|externalisee|Neutralise|Non disponible') {
            $isLimitation = $true
            $penalties.excludedLimitations++
        }
        
        if ($isLimitation) {
            # Ne pas penaliser les limitations techniques
            continue
        }
        
        if ($errType -match 'CRITICAL|FATAL') { 
            $penalties.critical++
            $topPenalties += [ordered]@{ type = 'CRITICAL'; source = $errSource; penalty = 25; msg = $errMsg }
        }
        elseif ($errType -match 'COLLECTOR_ERROR') { 
            $penalties.collectorErrors++
            $topPenalties += [ordered]@{ type = 'COLLECTOR_ERROR'; source = $errSource; penalty = 3; msg = $errMsg }
        }
        elseif ($errType -match 'TIMEOUT') { 
            $penalties.timeouts++
            $topPenalties += [ordered]@{ type = 'TIMEOUT'; source = $errSource; penalty = 5; msg = $errMsg }
        }
        elseif ($errType -match 'WARN|WARNING|TEMP_WARN') { 
            $penalties.warnings++
        }
        elseif ($errType -match 'INFO|TEMP_INFO') { 
            $penalties.infoIssues++
        }
    }
    
    # Calculer le score avec caps
    $totalPenalty = 0
    $totalPenalty += [math]::Min($penalties.critical, 2) * 25  # Max 2 critiques comptés
    $totalPenalty += $penalties.collectorErrors * 3
    $totalPenalty += [math]::Min($penalties.warnings, 10) * 1  # Max 10 warns comptés
    $totalPenalty += $penalties.timeouts * 5
    # INFO et limitations techniques ne penalisent pas le score
    
    # Score minimum 10 (sauf si 0 sections collectées)
    $finalScore = [math]::Max(10, [math]::Min(100, $baseScore - $totalPenalty))
    
    # Determiner le grade
    $grade = 'F'
    if ($finalScore -ge 90) { $grade = 'A' }
    elseif ($finalScore -ge 75) { $grade = 'B' }
    elseif ($finalScore -ge 50) { $grade = 'C' }
    elseif ($finalScore -ge 25) { $grade = 'D' }
    
    # Top 5 penalites
    $sortedPenalties = $topPenalties | Sort-Object { $_.penalty } -Descending | Select-Object -First 5
    
    return [ordered]@{
        score = $finalScore
        baseScore = $baseScore
        totalPenalty = $totalPenalty
        breakdown = $penalties
        grade = $grade
        topPenalties = @($sortedPenalties)
    }
}

function Set-CollectorStatus {
    param([string]$Name, [string]$Status, [string]$Message = '')
    try {
        if ([string]::IsNullOrEmpty($Name)) { return }
        $Script:CollectorStatus[$Name] = [ordered]@{
            status = $Status
            message = $Message
            timestamp = (Get-Date).ToString('o')
        }
    } catch { }
}

function Get-ErrorLogData {
    $errors = @()
    try {
        foreach ($err in @($Script:ErrorLog)) {
            if ($null -eq $err) { continue }
            $errors += [ordered]@{
                timestamp = Get-SafePropValue $err 'Timestamp' ''
                type = Get-SafePropValue $err 'Type' 'UNKNOWN'
                source = Get-SafePropValue $err 'Source' 'Unknown'
                message = Get-SafePropValue $err 'Message' 'No message'
            }
        }
    } catch { }
    return $errors
}

function Try-HardenOutputAcl {
    param([string]$Path)
    try {
        if (-not $Script:HardenOutputAcl) { return }
        if (-not (Test-Path $Path)) { return }
        $acl = Get-Acl -Path $Path
        $inherit = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit, [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
        $propagation = [System.Security.AccessControl.PropagationFlags]::None
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule("Administrators","FullControl",$inherit,$propagation,"Allow")
        $acl.SetAccessRuleProtection($true, $false)
        $acl.ResetAccessRule($rule)
        Set-Acl -Path $Path -AclObject $acl
    } catch {
        Add-ErrorLog -Type 'ACL_WARN' -Source 'Try-HardenOutputAcl' -Message $_.Exception.Message
    }
}

function Write-ReportLine { 
    param([string]$Line = "") 
    try { [void]$Script:ReportLines.Add($Line) } catch { } 
}

function Write-Section {
    param([string]$Title, [string[]]$Content)
    try {
        Write-ReportLine ""; Write-ReportLine ("=" * 100)
        Write-ReportLine "[$Title]"; Write-ReportLine ("=" * 100)
        $contentArray = @($Content)
        foreach ($line in $contentArray) { Write-ReportLine $line }
    }
    catch { }
}

function Format-Bytes {
    param([long]$Bytes)
    try {
        if ($Bytes -ge 1TB) { return "{0:N2} TB" -f ($Bytes / 1TB) }
        if ($Bytes -ge 1GB) { return "{0:N2} GB" -f ($Bytes / 1GB) }
        if ($Bytes -ge 1MB) { return "{0:N2} MB" -f ($Bytes / 1MB) }
        if ($Bytes -ge 1KB) { return "{0:N2} KB" -f ($Bytes / 1KB) }
        return "$Bytes B"
    }
    catch { return "0 B" }
}

function Get-RegistryValue {
    param([string]$Path, [string]$Name, [object]$Default = $null)
    try { return (Get-ItemPropertyValue -Path $Path -Name $Name -ErrorAction Stop) }
    catch { return $Default }
}

function Invoke-PreflightCheck {
    param([string]$ScriptPath, [switch]$SkipCheck)
    if ($SkipCheck) { return $true }
    try {
        $policy = Get-ExecutionPolicy
        if ($policy -in @('Restricted','Default')) {
            Write-Host "[PREFLIGHT] Policy: $policy" -ForegroundColor Yellow
            return $false
        }
        return $true
    }
    catch { return $true }
}
#endregion

#region ============== COLLECTEURS BRUTS ==============
function Collect-MachineIdentity {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "IDENTIFICATION DU SYSTEME"; $content += ("-" * 50)
        
        $data['computerName'] = Protect-ComputerName $env:COMPUTERNAME
        $data['username'] = Protect-Username $env:USERNAME
        $data['domain'] = $env:USERDOMAIN
        
        $content += "Nom Machine              : $(Get-SafeDictValue $data 'computerName' 'N/A')"
        $content += "Utilisateur              : $(Get-SafeDictValue $data 'username' 'N/A')"
        $content += "Domaine                  : $(Get-SafeDictValue $data 'domain' 'N/A')"

        $os = Get-WmiSafe -ClassName 'Win32_OperatingSystem'
        if ($null -ne $os) {
            $data['osCaption'] = Get-SafePropValue $os 'Caption' 'N/A'
            $data['osVersion'] = Get-SafePropValue $os 'Version' 'N/A'
            $data['osBuild'] = Get-SafePropValue $os 'BuildNumber' 'N/A'
            $content += ""; $content += "VERSION WINDOWS"; $content += ("-" * 50)
            $content += "Edition                  : $(Get-SafeDictValue $data 'osCaption' 'N/A')"
            $content += "Version                  : $(Get-SafeDictValue $data 'osVersion' 'N/A')"
            $content += "Build                    : $(Get-SafeDictValue $data 'osBuild' 'N/A')"
            
            $lastBoot = Get-SafePropValue $os 'LastBootUpTime'
            if ($null -ne $lastBoot) {
                try {
                    $uptime = (Get-Date) - $lastBoot
                    $data['lastBoot'] = $lastBoot.ToString('o')
                    $data['uptimeDays'] = ([math]::Max(0, $uptime.Days))
                    $data['uptimeHours'] = ([math]::Max(0, $uptime.Hours))
                    $content += ""; $content += "UPTIME"; $content += ("-" * 50)
                    $content += "Dernier Boot             : $($lastBoot.ToString('yyyy-MM-dd HH:mm'))"
                    $content += "Uptime                   : $(([math]::Max(0, $uptime.Days)))j $(([math]::Max(0, $uptime.Hours)))h"
                }
                catch { }
            }
        }

        $cs = Get-WmiSafe -ClassName 'Win32_ComputerSystem'
        if ($null -ne $cs) {
            $data['manufacturer'] = Get-SafePropValue $cs 'Manufacturer' 'N/A'
            $data['model'] = Get-SafePropValue $cs 'Model' 'N/A'
            $content += ""; $content += "FABRICANT"; $content += ("-" * 50)
            $content += "Fabricant                : $(Get-SafeDictValue $data 'manufacturer' 'N/A')"
            $content += "Modele                   : $(Get-SafeDictValue $data 'model' 'N/A')"
        }

        $bios = Get-WmiSafe -ClassName 'Win32_BIOS'
        if ($null -ne $bios) {
            $rawSerial = Get-SafePropValue $bios 'SerialNumber' ''
            $data['biosSerial'] = Protect-SerialNumber $rawSerial
            $data['biosVersion'] = Get-SafePropValue $bios 'SMBIOSBIOSVersion' 'N/A'
            $content += ""; $content += "BIOS"; $content += ("-" * 50)
            $content += "Serial                   : $(Get-SafeDictValue $data 'biosSerial' 'N/A')"
            $content += "Version                  : $(Get-SafeDictValue $data 'biosVersion' 'N/A')"
        }

        try {
            $secureBoot = Confirm-SecureBootUEFI -ErrorAction Stop
            $data['secureBoot'] = $secureBoot
            $content += "Secure Boot              : $(if($secureBoot){'ACTIVE'}else{'INACTIF'})"
        }
        catch { $data['secureBoot'] = $null }

        $tpm = Get-WmiSafe -ClassName 'Win32_Tpm' -Namespace "root\cimv2\security\microsofttpm"
        if ($null -ne $tpm) {
            $data['tpmPresent'] = $true
            $data['tpmVersion'] = Get-SafePropValue $tpm 'SpecVersion' 'N/A'
            $content += ""; $content += "TPM"; $content += ("-" * 50)
            $content += "TPM Version              : $(Get-SafeDictValue $data 'tpmVersion' 'N/A')"
        } else { $data['tpmPresent'] = $false }
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-MachineIdentity' -Message $_.Exception.Message }
    $Script:SectionData['MachineIdentity'] = $data
    return $content
}

function Collect-OSInfo {
    $content = @(); $data = [ordered]@{}
    try {
        $os = Get-WmiSafe -ClassName 'Win32_OperatingSystem'
        $content += "INFORMATIONS WINDOWS"; $content += ("-" * 50)
        if ($null -ne $os) {
            $data['caption'] = Get-SafePropValue $os 'Caption' 'N/A'
            $data['architecture'] = Get-SafePropValue $os 'OSArchitecture' 'N/A'
            $content += "Nom OS                   : $(Get-SafeDictValue $data 'caption' 'N/A')"
            $content += "Architecture             : $(Get-SafeDictValue $data 'architecture' 'N/A')"
            $displayVersion = Get-RegistryValue -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" -Name "DisplayVersion"
            $data['displayVersion'] = $displayVersion
            $content += "Display Version          : $displayVersion"
        }
        $content += ""; $content += "LICENCE"; $content += ("-" * 50)
        try {
            $license = Get-CimInstance -ClassName SoftwareLicensingProduct -ErrorAction Stop | 
                Where-Object { (Get-SafePropValue $_ 'Name' '') -like "*Windows*" -and (Get-SafePropValue $_ 'LicenseStatus' 0) -eq 1 } | 
                Select-Object -First 1
            if ($null -ne $license) {
                $data['licenseStatus'] = 'Active'
                $data['licensePartialKey'] = Protect-ProductKey (Get-SafePropValue $license 'PartialProductKey' 'N/A')
                $content += "Licence                  : Active ($(Get-SafeDictValue $data 'licensePartialKey' 'N/A'))"
            } else { $data['licenseStatus'] = 'Inactive'; $content += "Licence                  : Non activee" }
        }
        catch { $data['licenseStatus'] = 'Unknown'; $content += "Licence                  : [Erreur lecture]" }
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-OSInfo' -Message $_.Exception.Message }
    $Script:SectionData['OS'] = $data
    return $content
}

function Collect-CPU {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "PROCESSEUR(S)"; $content += ("-" * 50)
        $cpus = Get-WmiSafe -ClassName 'Win32_Processor'
        $cpuList = @()
        $cpuArray = @($cpus)
        foreach ($cpu in $cpuArray) {
            if ($null -eq $cpu) { continue }
            $cpuInfo = [ordered]@{
                name = Get-SafePropValue $cpu 'Name' 'N/A'
                cores = Get-SafePropValue $cpu 'NumberOfCores' 0
                threads = Get-SafePropValue $cpu 'NumberOfLogicalProcessors' 0
                maxClockSpeed = Get-SafePropValue $cpu 'MaxClockSpeed' 0
                currentLoad = Get-SafePropValue $cpu 'LoadPercentage' 0
            }
            $content += "CPU: $(Get-SafeDictValue $cpuInfo 'name' 'N/A')"
            $content += "  Cores/Threads          : $(Get-SafeDictValue $cpuInfo 'cores' 0) / $(Get-SafeDictValue $cpuInfo 'threads' 0)"
            $content += "  Frequence Max          : $(Get-SafeDictValue $cpuInfo 'maxClockSpeed' 0) MHz"
            $content += "  Charge Actuelle        : $(Get-SafeDictValue $cpuInfo 'currentLoad' 0)%"
            $cpuList += $cpuInfo
        }
        $data['cpus'] = $cpuList
        $data['cpuCount'] = Get-SafeCount $cpuList
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-CPU' -Message $_.Exception.Message }
    $Script:SectionData['CPU'] = $data
    return $content
}

function Collect-Memory {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "MEMOIRE PHYSIQUE (RAM)"; $content += ("-" * 50)
        $os = Get-WmiSafe -ClassName 'Win32_OperatingSystem'
        if ($null -ne $os) {
            $totalKB = Get-SafePropValue $os 'TotalVisibleMemorySize' 0
            $freeKB = Get-SafePropValue $os 'FreePhysicalMemory' 0
            $totalGB = [math]::Round($totalKB / 1MB, 2)
            $freeGB = [math]::Round($freeKB / 1MB, 2)
            $usedPercent = 0
            if ($totalKB -gt 0) { $usedPercent = [math]::Round((($totalKB - $freeKB) / $totalKB) * 100, 1) }
            $data['totalGB'] = $totalGB; $data['freeGB'] = $freeGB; $data['usedPercent'] = $usedPercent
            $content += "Memoire Totale           : $totalGB GB"
            $content += "Memoire Libre            : $freeGB GB"
            $content += "Utilisation              : $usedPercent%"
        }
        $memModules = Get-WmiSafe -ClassName 'Win32_PhysicalMemory'
        $moduleList = @()
        if ($null -ne $memModules) {
            $content += ""; $content += "MODULES"; $content += ("-" * 50)
            $moduleArray = @($memModules)
            foreach ($m in $moduleArray) {
                if ($null -eq $m) { continue }
                $capacity = Get-SafePropValue $m 'Capacity' 0
                $capacityGB = [math]::Round((Convert-SafeLong $capacity) / 1GB, 2)
                $memTypeCode = Get-SafePropValue $m 'SMBIOSMemoryType' 0
                $memType = switch($memTypeCode) { 20{'DDR'} 21{'DDR2'} 24{'DDR3'} 26{'DDR4'} 34{'DDR5'} default{"Type$memTypeCode"} }
                $speed = Get-SafePropValue $m 'Speed' 0
                $slot = Get-SafePropValue $m 'DeviceLocator' 'N/A'
                $moduleInfo = [ordered]@{ slot = $slot; capacityGB = $capacityGB; type = $memType; speedMHz = $speed }
                $content += "[$slot] $capacityGB GB $memType @ $speed MHz"
                $moduleList += $moduleInfo
            }
        }
        $data['modules'] = $moduleList; $data['moduleCount'] = Get-SafeCount $moduleList
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-Memory' -Message $_.Exception.Message }
    $Script:SectionData['Memory'] = $data
    return $content
}

function Collect-Storage {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "DISQUES PHYSIQUES"; $content += ("-" * 50)
        $disks = Get-WmiSafe -ClassName 'Win32_DiskDrive'
        $diskList = @()
        if ($null -ne $disks) {
            $diskArray = @($disks)
            foreach ($d in $diskArray) {
                if ($null -eq $d) { continue }
                $model = Get-SafePropValue $d 'Model' 'N/A'
                $size = Get-SafePropValue $d 'Size' 0
                $sizeGB = [math]::Round((Convert-SafeLong $size) / 1GB, 2)
                $status = Get-SafePropValue $d 'Status' 'Unknown'
                $interface = Get-SafePropValue $d 'InterfaceType' ''
                $serial = Protect-SerialNumber (Get-SafePropValue $d 'SerialNumber' '')
                $diskType = 'HDD'
                if ($model -match 'SSD|NVMe' -or $interface -eq 'NVMe') { $diskType = 'SSD' }
                $diskInfo = [ordered]@{ model = $model; type = $diskType; sizeGB = $sizeGB; status = $status; serial = $serial; interface = $interface }
                $content += "Disque: $model"; $content += "  Type: $diskType | Taille: $sizeGB GB | Status: $status"
                $diskList += $diskInfo
            }
        }
        $data['physicalDisks'] = $diskList; $data['physicalDiskCount'] = Get-SafeCount $diskList

        $content += ""; $content += "VOLUMES"; $content += ("-" * 50)
        $volumes = Get-WmiSafe -ClassName 'Win32_LogicalDisk'
        $volumeList = @()
        if ($null -ne $volumes) {
            $volumeArray = @($volumes)
            foreach ($v in $volumeArray) {
                if ($null -eq $v) { continue }
                $driveType = Get-SafePropValue $v 'DriveType' 0
                if ($driveType -ne 3) { continue }
                $size = Get-SafePropValue $v 'Size' 0
                if ((Convert-SafeLong $size) -le 0) { continue }
                $freeSpace = Get-SafePropValue $v 'FreeSpace' 0
                $letter = Get-SafePropValue $v 'DeviceID' 'N/A'
                $totalGB = [math]::Round((Convert-SafeLong $size) / 1GB, 2)
                $freeGB = [math]::Round((Convert-SafeLong $freeSpace) / 1GB, 2)
                $usedPercent = [math]::Round((((Convert-SafeLong $size) - (Convert-SafeLong $freeSpace)) / (Convert-SafeLong $size)) * 100, 1)
                $volumeInfo = [ordered]@{ letter = $letter; totalGB = $totalGB; freeGB = $freeGB; usedPercent = $usedPercent }
                $content += "$letter $totalGB GB | Libre: $freeGB GB ($usedPercent% utilise)"
                $volumeList += $volumeInfo
            }
        }
        $data['volumes'] = $volumeList; $data['volumeCount'] = Get-SafeCount $volumeList

        $content += ""; $content += "SMART"; $content += ("-" * 50)
        $smart = Get-WmiSafe -ClassName 'MSStorageDriver_FailurePredictStatus' -Namespace 'root\wmi'
        $smartList = @()
        if ($null -ne $smart) {
            $smartArray = @($smart)
            foreach ($s in $smartArray) {
                if ($null -eq $s) { continue }
                $predictFailure = Get-SafePropValue $s 'PredictFailure' $false
                $instanceName = Get-SafePropValue $s 'InstanceName' 'N/A'
                $smartInfo = [ordered]@{ instanceName = $instanceName; predictFailure = $predictFailure }
                $status = "[OK]"
                if ($predictFailure) { $status = "[ALERTE] Defaillance prevue!" }
                $content += $status
                $smartList += $smartInfo
            }
        } else { $content += "[INFO] SMART non disponible via WMI" }
        $data['smart'] = $smartList
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-Storage' -Message $_.Exception.Message }
    $Script:SectionData['Storage'] = $data
    return $content
}

function Collect-GPU {
    <#
    .SYNOPSIS
        Collecte GPU v7.0 - Données fiables uniquement
    .DESCRIPTION
        VRAM et température GPU neutralisées (limitation WMI - collecte externalisée)
        Conserve: Nom, Fabricant, Driver, Date, Résolution, PNPDeviceID
    #>
    $content = @()
    $data = [ordered]@{}
    try {
        $content += "CARTES GRAPHIQUES"
        $content += ("-" * 50)
        $gpuList = @()

        $gpusWmi = Get-WmiSafe -ClassName 'Win32_VideoController'
        $wmiArray = @($gpusWmi)
        $wmiCount = Get-SafeCount $wmiArray

        for ($i = 0; $i -lt $wmiCount; $i++) {
            $gpu = $wmiArray[$i]
            if ($null -eq $gpu) { continue }

            $name = Get-SafePropValue $gpu 'Name' 'Unknown'
            $driver = Get-SafePropValue $gpu 'DriverVersion' ''
            $driverDate = Get-SafePropValue $gpu 'DriverDate' ''
            $status = Get-SafePropValue $gpu 'Status' 'Unknown'
            $hRes = Convert-SafeInt (Get-SafePropValue $gpu 'CurrentHorizontalResolution' 0)
            $vRes = Convert-SafeInt (Get-SafePropValue $gpu 'CurrentVerticalResolution' 0)
            $pnpRaw = Get-SafePropValue $gpu 'PNPDeviceID' ''
            $vendor = Get-GpuVendor -Name $name -PnpDeviceId $pnpRaw
            $pnpDeviceId = $null
            if ($pnpRaw) { $pnpDeviceId = Protect-PnpDeviceId $pnpRaw }

            # v7.0: VRAM neutralisée (limitation WMI)
            $gpuInfo = [ordered]@{
                name = $name
                vendor = $vendor
                driverVersion = $driver
                driverDate = $driverDate
                pnpDeviceId = $pnpDeviceId
                resolution = "${hRes}x${vRes}"
                status = $status
                vramTotalMB = $null
                vramNote = 'Non disponible (limitation WMI - collecte externalisee)'
            }
            $gpuList += $gpuInfo

            $content += "GPU: $name"
            $content += "  Fabricant: $vendor"
            $content += "  Driver: $driver"
            $content += "  Resolution: ${hRes}x${vRes}"
            $content += "  VRAM: Non disponible (limitation WMI - collecte externalisee)"
        }

        if ((Get-SafeCount $gpuList) -eq 0) { $content += "[INFO] Aucun GPU detecte" }

        $data['gpuList'] = $gpuList
        $data['gpus'] = $gpuList
        $data['gpuCount'] = Get-SafeCount $gpuList
    } catch {
        Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-GPU' -Message $_.Exception.Message
    }
    $Script:SectionData['GPU'] = $data
    return $content
}

#
# Collecte des températures CPU/GPU/Disques
# v7.0: CPU et GPU neutralisés (limitation WMI - collecte externalisée)
# Seules les températures disques sont conservées si disponibles
function Collect-Temperatures {
    <#
    .SYNOPSIS
        Collecte températures v7.0 - Données fiables uniquement
    .DESCRIPTION
        CPU et GPU: Neutralisés (limitation WMI - collecte externalisée vers Brique 2)
        Disques: Conservés via StorageReliabilityCounter/SMART si disponibles
    #>
    $content = @()
    $data = [ordered]@{}
    $degC = "$([char]176)C"

    try {
        $content += "TEMPERATURES HARDWARE"
        $content += ("-" * 50)

        # v7.0: CPU et GPU neutralisés
        $cpuNote = 'Non disponible (limitation WMI - collecte externalisee)'
        $gpuNote = 'Non disponible (limitation WMI - collecte externalisee)'
        
        $content += ""
        $content += "CPU: $cpuNote"
        $content += "GPU: $gpuNote"
        
        # Températures disques (conservées)
        $diskTemps = @((Get-DiskTemperatures))
        if ($null -eq $diskTemps) { $diskTemps = @() }
        
        $content += "Disques: $(Get-SafeCount $diskTemps)"
        foreach ($disk in $diskTemps) {
            $model = Get-SafeDictValue $disk 'model' 'Disque'
            $temp = Get-SafeDictValue $disk 'tempC' $null
            $source = Get-SafeDictValue $disk 'source' 'Unknown'
            $tempLabel = 'N/A'
            if ($null -ne $temp) { $tempLabel = "$temp $degC" }
            $content += "  - $model : $tempLabel [$source]"
        }
        if ((Get-SafeCount $diskTemps) -eq 0) {
            $content += "[INFO] Temperature disque non disponible"
        }

        # Données JSON
        $data['cpuTempC'] = $null
        $data['gpuTempC'] = $null
        $data['cpuNote'] = $cpuNote
        $data['gpuNote'] = $gpuNote
        $data['disks'] = $diskTemps
        $data['cpuSource'] = 'Neutralise_v7'
        $data['gpuSource'] = 'Neutralise_v7'
        $data['lhmAvailable'] = $false
        $Script:SectionData['Temperatures'] = $data
    }
    catch {
        Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-Temperatures' -Message $_.Exception.Message
        $data['cpuTempC'] = $null
        $data['gpuTempC'] = $null
        $data['cpuNote'] = 'Erreur collecte'
        $data['gpuNote'] = 'Erreur collecte'
        $data['disks'] = @()
        $data['cpuSource'] = 'Erreur'
        $data['gpuSource'] = 'Erreur'
        $data['lhmAvailable'] = $false
        $Script:SectionData['Temperatures'] = $data
        $content += "[ERREUR] Collecte temperatures echouee"
    }

    return $content
}

function Collect-Network {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "CONFIGURATION RESEAU"; $content += ("-" * 50)
        $ipConfigs = Get-WmiSafe -ClassName 'Win32_NetworkAdapterConfiguration'
        $adapterList = @()
        if ($null -ne $ipConfigs) {
            $configArray = @($ipConfigs)
            foreach ($ip in $configArray) {
                if ($null -eq $ip) { continue }
                $ipEnabled = Get-SafePropValue $ip 'IPEnabled' $false
                if (-not $ipEnabled) { continue }
                $description = Get-SafePropValue $ip 'Description' 'N/A'
                $content += "Interface: $description"
                $ipAddresses = @(Get-SafePropValue $ip 'IPAddress' @())
                $maskedIPs = @(); foreach ($addr in $ipAddresses) { if ($addr) { $maskedIPs += Protect-IPAddress $addr } }
                $gateways = @(Get-SafePropValue $ip 'DefaultIPGateway' @())
                $maskedGateways = @(); foreach ($gw in $gateways) { if ($gw) { $maskedGateways += Protect-IPAddress $gw } }
                $mac = Protect-MACAddress (Get-SafePropValue $ip 'MACAddress' '')
                $dhcp = Get-SafePropValue $ip 'DHCPEnabled' $false
                $dns = @(Get-SafePropValue $ip 'DNSServerSearchOrder' @())
                $adapterInfo = [ordered]@{ name = $description; ip = $maskedIPs; gateway = $maskedGateways; mac = $mac; dhcp = $dhcp; dns = $dns }
                $content += "  IP: $($maskedIPs -join ', ') | Gateway: $($maskedGateways -join ', ') | MAC: $mac"
                $adapterList += $adapterInfo
            }
        }
        $data['adapters'] = $adapterList; $data['adapterCount'] = Get-SafeCount $adapterList

        $content += ""; $content += "CONNEXIONS"; $content += ("-" * 50)
        try {
            $connections = @(Get-NetTCPConnection -ErrorAction Stop | Where-Object { (Get-SafePropValue $_ 'State' '') -ne 'TimeWait' })
            $listening = @($connections | Where-Object { (Get-SafePropValue $_ 'State' '') -eq 'Listen' })
            $data['totalConnections'] = Get-SafeCount $connections
            $data['listeningPorts'] = Get-SafeCount $listening
            $content += "Total connexions: $(Get-SafeDictValue $data 'totalConnections' 0) | Ports en ecoute: $(Get-SafeDictValue $data 'listeningPorts' 0)"
        }
        catch { $content += "[INFO] Get-NetTCPConnection non disponible"; $data['totalConnections'] = 0; $data['listeningPorts'] = 0 }
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-Network' -Message $_.Exception.Message }
    $Script:SectionData['Network'] = $data
    return $content
}

function Collect-Security {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "WINDOWS DEFENDER"; $content += ("-" * 50)
        try {
            $defender = Get-MpComputerStatus -ErrorAction Stop
            $data['defenderRTP'] = Get-SafePropValue $defender 'RealTimeProtectionEnabled' $null
            $data['defenderEnabled'] = Get-SafePropValue $defender 'AntivirusEnabled' $null
            $data['defenderSignatureAge'] = Get-SafePropValue $defender 'AntivirusSignatureAge' -1
            $content += "Real-time Protection     : $(Get-SafeDictValue $data 'defenderRTP' 'N/A')"
            $content += "Antivirus Enabled        : $(Get-SafeDictValue $data 'defenderEnabled' 'N/A')"
            $content += "Signature Age (jours)    : $(Get-SafeDictValue $data 'defenderSignatureAge' 'N/A')"
        }
        catch { $content += "[INFO] Defender non disponible" }

        $content += ""; $content += "ANTIVIRUS (Security Center)"; $content += ("-" * 50)
        $avProducts = Get-WmiSafe -ClassName 'AntiVirusProduct' -Namespace 'root\SecurityCenter2'
        $avList = @()
        if ($null -ne $avProducts) {
            $avArray = @($avProducts)
            foreach ($av in $avArray) {
                if ($null -eq $av) { continue }
                $displayName = Get-SafePropValue $av 'displayName' 'N/A'
                $content += "Produit: $displayName"
                $avList += $displayName
            }
        } else { $content += "[INFO] Aucun antivirus detecte via Security Center" }
        $data['antivirusProducts'] = $avList

        $content += ""; $content += "PARE-FEU"; $content += ("-" * 50)
        $fwData = [ordered]@{}
        try {
            $fw = Get-NetFirewallProfile -ErrorAction Stop
            $fwArray = @($fw)
            foreach ($profile in $fwArray) {
                if ($null -eq $profile) { continue }
                $profileName = Get-SafePropValue $profile 'Name' 'N/A'
                $profileEnabled = Get-SafePropValue $profile 'Enabled' $false
                $status = "DESACTIVE"
                if ($profileEnabled) { $status = "ACTIVE" }
                $content += "$profileName : $status"
                $fwData[$profileName] = $profileEnabled
            }
        }
        catch { $content += "[INFO] Get-NetFirewallProfile non disponible" }
        $data['firewall'] = $fwData

        $content += ""; $content += "UAC"; $content += ("-" * 50)
        $uac = Get-RegistryValue -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name "EnableLUA" -Default 0
        $data['uacEnabled'] = ($uac -eq 1)
        $content += "UAC: $(if($uac -eq 1){'Active'}else{'Desactive'})"
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-Security' -Message $_.Exception.Message }
    $Script:SectionData['Security'] = $data
    return $content
}

function Collect-Services {
    $content = @(); $data = [ordered]@{}
    try {
        $criticalServices = @('wuauserv', 'WinDefend', 'mpssvc', 'EventLog', 'CryptSvc')
        $content += "SERVICES CRITIQUES"; $content += ("-" * 50)
        $criticalList = @()
        foreach ($svcName in $criticalServices) {
            try {
                $svc = Get-Service -Name $svcName -ErrorAction Stop
                $status = Get-SafePropValue $svc 'Status' 'Unknown'
                $displayName = Get-SafePropValue $svc 'DisplayName' $svcName
                $icon = '[STOP]'
                if ("$status" -eq 'Running') { $icon = '[OK]' }
                $content += "$icon $displayName"
                $criticalList += [ordered]@{ name = $svcName; displayName = $displayName; status = "$status" }
            }
            catch { $content += "[N/A] $svcName"; $criticalList += [ordered]@{ name = $svcName; displayName = $svcName; status = 'NotFound' } }
        }
        $data['criticalServices'] = $criticalList
        try {
            $all = @(Get-Service -ErrorAction Stop)
            $running = @($all | Where-Object { (Get-SafePropValue $_ 'Status' '') -eq 'Running' })
            $data['totalServices'] = Get-SafeCount $all
            $data['runningServices'] = Get-SafeCount $running
            $content += ""; $content += "Total: $(Get-SafeDictValue $data 'totalServices' 0) | Running: $(Get-SafeDictValue $data 'runningServices' 0)"
        }
        catch { $data['totalServices'] = 0; $data['runningServices'] = 0 }
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-Services' -Message $_.Exception.Message }
    $Script:SectionData['Services'] = $data
    return $content
}

function Collect-StartupPrograms {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "PROGRAMMES AU DEMARRAGE"; $content += ("-" * 50)
        $startupList = @()
        $runKeys = @(@{Path='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'; Scope='Machine'}, @{Path='HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'; Scope='User'})
        foreach ($key in $runKeys) {
            try {
                $items = Get-ItemProperty -Path (Get-SafeDictValue $key 'Path' '') -ErrorAction SilentlyContinue
                if ($null -ne $items -and $null -ne $items.PSObject -and $null -ne $items.PSObject.Properties) {
                    foreach ($prop in $items.PSObject.Properties) {
                        $propName = Get-SafePropValue $prop 'Name' ''
                        if ($propName -match '^PS') { continue }
                        $scope = Get-SafeDictValue $key 'Scope' 'Unknown'
                        $content += "[$scope] $propName"
                        $startupList += [ordered]@{ name = $propName; scope = $scope }
                    }
                }
            }
            catch { }
        }
        $data['startupItems'] = $startupList; $data['startupCount'] = Get-SafeCount $startupList
        $content += ""; $content += "Total: $(Get-SafeDictValue $data 'startupCount' 0) elements"
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-StartupPrograms' -Message $_.Exception.Message }
    $Script:SectionData['StartupPrograms'] = $data
    return $content
}

function Collect-HealthChecks {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "VERIFICATION SANTE SYSTEME"; $content += ("=" * 50); $content += ""; $content += "REDEMARRAGE REQUIS"; $content += ("-" * 50)
        $rebootRequired = $false; $rebootReasons = @()
        if (Test-Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") { $rebootRequired = $true; $rebootReasons += "Windows Update" }
        if (Test-Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") { $rebootRequired = $true; $rebootReasons += "Composants CBS" }
        $data['rebootRequired'] = $rebootRequired; $data['rebootReasons'] = $rebootReasons
        if ($rebootRequired) { $content += "[ATTENTION] Redemarrage requis: $($rebootReasons -join ', ')" }
        else { $content += "[OK] Aucun redemarrage en attente" }
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-HealthChecks' -Message $_.Exception.Message }
    $Script:SectionData['HealthChecks'] = $data
    return $content
}

function Collect-EventLogs {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "JOURNAUX D'EVENEMENTS"; $content += ("-" * 50)
        $logs = @('System', 'Application'); $logData = [ordered]@{}
        foreach ($log in $logs) {
            $content += ""; $content += "=== $log ==="
            try {
                $errors = @(Get-WinEvent -FilterHashtable @{ LogName = $log; Level = 1,2; StartTime = (Get-Date).AddDays(-7) } -MaxEvents 50 -ErrorAction SilentlyContinue)
                $critCount = Get-SafeCount ($errors | Where-Object { (Get-SafePropValue $_ 'Level' 0) -eq 1 })
                $errCount = Get-SafeCount ($errors | Where-Object { (Get-SafePropValue $_ 'Level' 0) -eq 2 })
                $logData[$log] = [ordered]@{ criticalCount = $critCount; errorCount = $errCount }
                $content += "Critiques: $critCount | Erreurs: $errCount (7 jours)"
            }
            catch { $logData[$log] = [ordered]@{ criticalCount = 0; errorCount = 0 }; $content += "[OK] Aucune erreur recente" }
        }
        $data['logs'] = $logData
        $content += ""; $content += "BSOD (30 jours)"; $content += ("-" * 50)
        try {
            $bsod = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WER-SystemErrorReporting'; StartTime = (Get-Date).AddDays(-30) } -MaxEvents 20 -ErrorAction SilentlyContinue)
            $data['bsodCount'] = Get-SafeCount $bsod
            $content += "BSOD detectes: $(Get-SafeDictValue $data 'bsodCount' 0)"
        }
        catch { $data['bsodCount'] = 0; $content += "[INFO] Non disponible" }
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-EventLogs' -Message $_.Exception.Message }
    $Script:SectionData['EventLogs'] = $data
    return $content
}

function Collect-WindowsUpdate {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "WINDOWS UPDATE"; $content += ("-" * 50)
        try {
            $session = New-Object -ComObject Microsoft.Update.Session
            $searcher = $session.CreateUpdateSearcher()
            $pending = $searcher.Search("IsInstalled=0")
            $pendingCount = 0
            if ($null -ne $pending -and $null -ne $pending.Updates) { $pendingCount = Get-SafePropValue $pending.Updates 'Count' 0 }
            $data['pendingCount'] = $pendingCount
            $content += "Mises a jour en attente  : $pendingCount"
        }
        catch { $data['pendingCount'] = -1; $content += "[INFO] Non disponible" }
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-WindowsUpdate' -Message $_.Exception.Message }
    $Script:SectionData['WindowsUpdate'] = $data
    return $content
}

function Collect-Audio {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "PERIPHERIQUES AUDIO"; $content += ("-" * 50)
        $audio = Get-WmiSafe -ClassName 'Win32_SoundDevice'
        $audioList = @()
        if ($null -ne $audio) {
            $audioArray = @($audio)
            foreach ($a in $audioArray) {
                if ($null -eq $a) { continue }
                $name = Get-SafePropValue $a 'Name' 'N/A'
                $status = Get-SafePropValue $a 'Status' 'Unknown'
                $content += "Device: $name - $status"
                $audioList += [ordered]@{ name = $name; status = $status }
            }
        }
        $data['devices'] = $audioList; $data['deviceCount'] = Get-SafeCount $audioList
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-Audio' -Message $_.Exception.Message }
    $Script:SectionData['Audio'] = $data
    return $content
}

function Collect-DevicesDrivers {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "PERIPHERIQUES AVEC PROBLEMES"; $content += ("-" * 50)
        $problemDevices = @()
        try {
            $devices = Get-PnpDevice -Status Error, Degraded, Unknown -ErrorAction Stop
            $deviceArray = @($devices)
            if ((Get-SafeCount $deviceArray) -gt 0) {
                foreach ($dev in $deviceArray) {
                    if ($null -eq $dev) { continue }
                    $friendlyName = Get-SafePropValue $dev 'FriendlyName' 'Unknown'
                    $class = Get-SafePropValue $dev 'Class' 'N/A'
                    $status = Get-SafePropValue $dev 'Status' 'Unknown'
                    $content += "[$status] $friendlyName"
                    $problemDevices += [ordered]@{ name = $friendlyName; class = $class; status = "$status" }
                }
            } else { $content += "[OK] Aucun peripherique en erreur" }
        }
        catch { $content += "[INFO] Get-PnpDevice non disponible" }
        $data['problemDevices'] = $problemDevices; $data['problemDeviceCount'] = Get-SafeCount $problemDevices
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-DevicesDrivers' -Message $_.Exception.Message }
    $Script:SectionData['DevicesDrivers'] = $data
    return $content
}

function Collect-InstalledApplications {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "APPLICATIONS INSTALLEES"; $content += ("-" * 50)
        $apps = @()
        
        # Methode 1: Registre 64-bit et 32-bit (HKLM + HKCU)
        $regPaths = @(
            "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
            "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
            "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
            "HKCU:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
        )
        foreach ($path in $regPaths) {
            try { 
                $items = Get-ItemProperty $path -ErrorAction SilentlyContinue | Where-Object { 
                    $_.DisplayName -and $_.DisplayName.Trim() -ne '' 
                }
                if ($null -ne $items) { $apps += @($items) }
            } catch { }
        }
        
        # Fallback si registre vide: Get-Package (PS 5.1+)
        if ((Get-SafeCount $apps) -eq 0) {
            try {
                $pkgs = Get-Package -ProviderName Programs -ErrorAction SilentlyContinue
                foreach ($pkg in @($pkgs)) {
                    if ($null -eq $pkg) { continue }
                    $apps += [PSCustomObject]@{ DisplayName = Get-SafePropValue $pkg 'Name' '' }
                }
            } catch { }
        }
        
        $uniqueApps = @($apps | Where-Object { $_.DisplayName } | Sort-Object DisplayName -Unique)
        $data['totalCount'] = Get-SafeCount $uniqueApps
        $data['source'] = 'Registry'
        if ((Get-SafeCount $uniqueApps) -eq 0) { 
            Add-MissingData -Section 'InstalledApplications' -Item 'AppList' -Reason 'Aucune application trouvee dans le registre'
        }
        $content += "Total: $(Get-SafeDictValue $data 'totalCount' 0) applications"
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-InstalledApplications' -Message $_.Exception.Message }
    $Script:SectionData['InstalledApplications'] = $data
    return $content
}

function Collect-ScheduledTasks {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "TACHES PLANIFIEES"; $content += ("-" * 50)
        try {
            $tasks = @(Get-ScheduledTask -ErrorAction Stop)
            $ready = @($tasks | Where-Object { (Get-SafePropValue $_ 'State' '') -eq 'Ready' })
            $data['totalTasks'] = Get-SafeCount $tasks; $data['readyTasks'] = Get-SafeCount $ready
            $content += "Total: $(Get-SafeDictValue $data 'totalTasks' 0) | Actives: $(Get-SafeDictValue $data 'readyTasks' 0)"
        }
        catch { $data['totalTasks'] = 0; $data['readyTasks'] = 0; $content += "[INFO] Get-ScheduledTask non disponible" }
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-ScheduledTasks' -Message $_.Exception.Message }
    $Script:SectionData['ScheduledTasks'] = $data
    return $content
}

function Collect-Processes {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "TOP 15 PROCESSUS (RAM)"; $content += ("-" * 50)
        $memList = @()
        $source = 'none'
        
        # Methode 1: Get-Process
        try {
            $procs = Get-Process -ErrorAction Stop | Sort-Object WorkingSet64 -Descending | Select-Object -First 15
            $procArray = @($procs)
            foreach ($p in $procArray) {
                if ($null -eq $p) { continue }
                $procName = Get-SafePropValue $p 'ProcessName' 'Unknown'
                $pid = Get-SafePropValue $p 'Id' 0
                $ws = Get-SafePropValue $p 'WorkingSet64' 0
                $memMB = [math]::Round((Convert-SafeLong $ws) / 1MB, 1)
                $content += "$procName (PID $pid) - $memMB MB"
                $memList += [ordered]@{ name = $procName; pid = $pid; memoryMB = $memMB }
            }
            $source = 'Get-Process'
        }
        catch {
            # Fallback: CIM Win32_Process
            try {
                $cimProcs = Get-CimInstance -ClassName Win32_Process -ErrorAction Stop | 
                    Sort-Object WorkingSetSize -Descending | Select-Object -First 15
                foreach ($p in @($cimProcs)) {
                    if ($null -eq $p) { continue }
                    $procName = Get-SafePropValue $p 'Name' 'Unknown'
                    $pid = Get-SafePropValue $p 'ProcessId' 0
                    $ws = Get-SafePropValue $p 'WorkingSetSize' 0
                    $memMB = [math]::Round((Convert-SafeLong $ws) / 1MB, 1)
                    $content += "$procName (PID $pid) - $memMB MB"
                    $memList += [ordered]@{ name = $procName; pid = $pid; memoryMB = $memMB }
                }
                $source = 'CIM'
            }
            catch { 
                $content += "[INFO] Collecte processus non disponible"
                Add-MissingData -Section 'Processes' -Item 'ProcessList' -Reason 'Get-Process et CIM ont echoue'
            }
        }
        $data['topMemory'] = $memList
        $data['source'] = $source
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-Processes' -Message $_.Exception.Message }
    $Script:SectionData['Processes'] = $data
    return $content
}

function Collect-Battery {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "BATTERIE"; $content += ("-" * 50)
        $bat = Get-WmiSafe -ClassName 'Win32_Battery'
        if ($null -ne $bat) {
            $data['present'] = $true; $data['chargePercent'] = Get-SafePropValue $bat 'EstimatedChargeRemaining' 0
            $content += "Charge: $(Get-SafeDictValue $data 'chargePercent' 0)%"
        } else { $data['present'] = $false; $content += "[INFO] Pas de batterie" }
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-Battery' -Message $_.Exception.Message }
    $Script:SectionData['Battery'] = $data
    return $content
}

function Collect-Printers {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "IMPRIMANTES"; $content += ("-" * 50)
        $printers = Get-WmiSafe -ClassName 'Win32_Printer'
        $printerList = @()
        if ($null -ne $printers) {
            $printerArray = @($printers)
            foreach ($p in $printerArray) {
                if ($null -eq $p) { continue }
                $name = Get-SafePropValue $p 'Name' 'N/A'
                $default = Get-SafePropValue $p 'Default' $false
                $defaultStr = ""
                if ($default) { $defaultStr = "[DEFAUT]" }
                $content += "$name $defaultStr"
                $printerList += [ordered]@{ name = $name; default = $default }
            }
        } else { $content += "Aucune imprimante" }
        $data['printers'] = $printerList; $data['printerCount'] = Get-SafeCount $printerList
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-Printers' -Message $_.Exception.Message }
    $Script:SectionData['Printers'] = $data
    return $content
}

function Collect-UserProfiles {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "PROFILS UTILISATEURS"; $content += ("-" * 50)
        $profiles = Get-WmiSafe -ClassName 'Win32_UserProfile'
        $profileList = @()
        if ($null -ne $profiles) {
            $profileArray = @($profiles)
            foreach ($p in $profileArray) {
                if ($null -eq $p) { continue }
                $special = Get-SafePropValue $p 'Special' $false
                if ($special) { continue }
                $localPath = Get-SafePropValue $p 'LocalPath' ''
                $maskedPath = Protect-ProfilePath $localPath
                $content += $maskedPath
                $profileList += [ordered]@{ path = $maskedPath }
            }
        }
        $data['profiles'] = $profileList; $data['profileCount'] = Get-SafeCount $profileList
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-UserProfiles' -Message $_.Exception.Message }
    $Script:SectionData['UserProfiles'] = $data
    return $content
}

function Collect-Virtualization {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "VIRTUALISATION"; $content += ("-" * 50)
        $isVM = $false
        $cs = Get-WmiSafe -ClassName 'Win32_ComputerSystem'
        if ($null -ne $cs) {
            $model = Get-SafePropValue $cs 'Model' ''
            if ($model -match 'VMware|VirtualBox|Virtual Machine|Hyper-V|KVM|QEMU') { $isVM = $true }
        }
        $data['isVM'] = $isVM
        $content += "Est une VM: $(if($isVM){'Oui'}else{'Non'})"
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-Virtualization' -Message $_.Exception.Message }
    $Script:SectionData['Virtualization'] = $data
    return $content
}

function Collect-RestorePoints {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "POINTS DE RESTAURATION"; $content += ("-" * 50)
        if ($Script:IsAdmin) {
            try {
                $rp = Get-ComputerRestorePoint -ErrorAction Stop | Select-Object -First 5
                $rpArray = @($rp)
                $data['restorePointCount'] = Get-SafeCount $rpArray
                if ((Get-SafeDictValue $data 'restorePointCount' 0) -gt 0) { $content += "Points disponibles: $(Get-SafeDictValue $data 'restorePointCount' 0)" }
                else { $content += "[INFO] Aucun point de restauration" }
            }
            catch { $data['restorePointCount'] = -1; $content += "[INFO] Get-ComputerRestorePoint non disponible" }
        } else { $data['restorePointCount'] = -1; $content += "[INFO] Admin requis" }
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-RestorePoints' -Message $_.Exception.Message }
    $Script:SectionData['RestorePoints'] = $data
    return $content
}

function Collect-TempFiles {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "FICHIERS TEMPORAIRES"; $content += ("-" * 50)
        $locations = @(@{Name='Temp User'; Path=$env:TEMP}, @{Name='Temp Windows'; Path="$env:WINDIR\Temp"})
        $totalTemp = 0; $tempData = [ordered]@{}
        foreach ($loc in $locations) {
            $locPath = Get-SafeDictValue $loc 'Path' ''; $locName = Get-SafeDictValue $loc 'Name' 'Unknown'
            if (Test-Path $locPath) {
                try {
                    $items = $null
                    if ($Script:MaxTempFiles -gt 0) {
                        $items = Get-ChildItem -Path $locPath -Recurse -Force -ErrorAction SilentlyContinue | Select-Object -First $Script:MaxTempFiles
                    } else {
                        $items = Get-ChildItem -Path $locPath -Recurse -Force -ErrorAction SilentlyContinue
                    }
                    $size = ($items | Measure-Object -Property Length -Sum).Sum
                    if ($null -eq $size) { $size = 0 }
                    $content += "$locName : $(Format-Bytes $size)"; $tempData[$locName] = $size; $totalTemp += $size
                } catch { $tempData[$locName] = 0 }
            }
        }
        $data['locations'] = $tempData; $data['totalBytes'] = $totalTemp
        $content += "TOTAL: $(Format-Bytes $totalTemp)"
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-TempFiles' -Message $_.Exception.Message }
    $Script:SectionData['TempFiles'] = $data
    return $content
}

function Collect-EnvironmentVariables {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "VARIABLES D'ENVIRONNEMENT"; $content += ("-" * 50)
        $sysVars = [Environment]::GetEnvironmentVariables([EnvironmentVariableTarget]::Machine)
        $varList = [ordered]@{}
        $sortedKeys = @($sysVars.Keys) | Sort-Object | Select-Object -First 20
        foreach ($key in $sortedKeys) {
            $val = Protect-EnvValue -Key "$key" -Value "$($sysVars[$key])"
            if ($val.Length -gt 80) { $val = $val.Substring(0, 80) + "..." }
            $content += "$key = $val"; $varList[$key] = $val
        }
        $data['variables'] = $varList
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-EnvironmentVariables' -Message $_.Exception.Message }
    $Script:SectionData['EnvironmentVariables'] = $data
    return $content
}

function Collect-Certificates {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "CERTIFICATS"; $content += ("-" * 50)
        $expiredCount = 0; $validCount = 0
        try {
            $certs = Get-ChildItem -Path 'Cert:\LocalMachine\My' -ErrorAction Stop | Select-Object -First 10
            $certArray = @($certs)
            foreach ($cert in $certArray) {
                if ($null -eq $cert) { continue }
                $notAfter = Get-SafePropValue $cert 'NotAfter'
                $subject = Protect-CertificateSubject (Get-SafePropValue $cert 'Subject' 'N/A')
                if ($subject.Length -gt 50) { $subject = $subject.Substring(0, 50) + "..." }
                if ($null -ne $notAfter -and $notAfter -lt (Get-Date)) { $expiredCount++; $content += "[EXPIRE] $subject" }
                else { $validCount++; $content += "[OK] $subject" }
            }
        }
        catch { $content += "[INFO] Acces aux certificats non disponible" }
        $data['expiredCount'] = $expiredCount; $data['validCount'] = $validCount
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-Certificates' -Message $_.Exception.Message }
    $Script:SectionData['Certificates'] = $data
    return $content
}

function Collect-RegistryKeys {
    $content = @(); $data = [ordered]@{}
    try { $content += "CLES REGISTRE"; $content += ("-" * 50); $content += "[INFO] Cles critiques verifiees dans autres collecteurs"; $data['note'] = 'Keys checked in other collectors' }
    catch { }
    $Script:SectionData['Registry'] = $data
    return $content
}

function Collect-Temperatures-Legacy {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "TEMPERATURES SYSTEME"; $content += ("-" * 50)
        $tempList = @()
        if ($Script:IsAdmin) {
            $zones = Get-WmiSafe -ClassName 'MSAcpi_ThermalZoneTemperature' -Namespace 'root\WMI'
            if ($null -ne $zones) {
                $zoneArray = @($zones)
                foreach ($z in $zoneArray) {
                    if ($null -eq $z) { continue }
                    $currentTemp = Get-SafePropValue $z 'CurrentTemperature' 0
                    $tempC = [math]::Round(($currentTemp / 10) - 273.15, 1)
                    $content += "Zone: $tempC C"; $tempList += $tempC
                }
                $data['available'] = $true
            } else { $content += "[INFO] Temperatures non disponibles via WMI"; $data['available'] = $false }
        } else { $content += "[INFO] Admin requis pour temperatures"; $data['available'] = $false }
        $data['temperatures'] = $tempList
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-Temperatures' -Message $_.Exception.Message }
    $Script:SectionData['Temperatures'] = $data
    return $content
}

function Collect-SystemIntegrity {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "INTEGRITE SYSTEME"; $content += ("-" * 50)
        $cbsPending = Test-Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"
        $data['cbsPending'] = $cbsPending
        if ($cbsPending) { $content += "[ATTENTION] Composants CBS en attente" }
        else { $content += "[OK] Aucune operation CBS en attente" }
        $content += ""; $content += "Commandes de verification:"; $content += "  sfc /scannow"; $content += "  DISM /Online /Cleanup-Image /ScanHealth"
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-SystemIntegrity' -Message $_.Exception.Message }
    $Script:SectionData['SystemIntegrity'] = $data
    return $content
}

function Collect-PowerSettings {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "PARAMETRES D'ALIMENTATION"; $content += ("-" * 50)
        $activePlanName = 'Unknown'
        $source = 'none'
        
        # Methode 1: powercfg /getactivescheme
        try {
            $activePlan = powercfg /getactivescheme 2>$null
            if ($null -ne $activePlan -and $activePlan.Length -gt 0) {
                # Format: "GUID du schema d'alimentation : GUID  (Nom du plan)"
                if ($activePlan -match '\(([^)]+)\)') {
                    $activePlanName = $Matches[1]
                    $source = 'powercfg'
                }
                elseif ($activePlan -match ':.*([0-9a-fA-F-]{36})') {
                    # Au moins on a le GUID
                    $activePlanName = "Plan $($Matches[1].Substring(0,8))"
                    $source = 'powercfg-guid'
                }
            }
        } catch { }
        
        # Fallback: Registre
        if ($activePlanName -eq 'Unknown') {
            try {
                $regGuid = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes' -Name 'ActivePowerScheme' -ErrorAction SilentlyContinue
                if ($null -ne $regGuid -and $regGuid.ActivePowerScheme) {
                    $guid = $regGuid.ActivePowerScheme
                    $planPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\$guid"
                    $planName = Get-ItemProperty -Path $planPath -Name 'FriendlyName' -ErrorAction SilentlyContinue
                    if ($planName -and $planName.FriendlyName) {
                        $activePlanName = $planName.FriendlyName -replace '@.*,',''
                    } else {
                        $activePlanName = "Plan $($guid.Substring(0,8))"
                    }
                    $source = 'Registry'
                }
            } catch { }
        }
        
        $data['activePlan'] = $activePlanName
        $data['source'] = $source
        $content += "Plan Actif: $activePlanName"
        
        $fastStartup = Get-RegistryValue -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power" -Name "HiberbootEnabled" -Default -1
        $data['fastStartup'] = $fastStartup
        $content += "Demarrage rapide: $(if($fastStartup -eq 1){'ACTIVE'}elseif($fastStartup -eq 0){'DESACTIVE'}else{'Non configure'})"
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-PowerSettings' -Message $_.Exception.Message }
    $Script:SectionData['PowerSettings'] = $data
    return $content
}

function Collect-MinidumpAnalysis {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "ANALYSE MINIDUMPS"; $content += ("-" * 50)
        $minidumpPath = "$env:SystemRoot\Minidump"
        if (Test-Path $minidumpPath) {
            try {
                $dumps = Get-ChildItem -Path $minidumpPath -Filter "*.dmp" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 10
                $dumpArray = @($dumps)
                $data['minidumpCount'] = Get-SafeCount $dumpArray
                if ((Get-SafeDictValue $data 'minidumpCount' 0) -gt 0) {
                    $content += "Minidumps trouves: $(Get-SafeDictValue $data 'minidumpCount' 0)"
                    $recentDumps = @()
                    foreach ($d in ($dumpArray | Select-Object -First 5)) {
                        if ($null -eq $d) { continue }
                        $lastWrite = Get-SafePropValue $d 'LastWriteTime'
                        $name = Get-SafePropValue $d 'Name' 'N/A'
                        if ($null -ne $lastWrite) { $content += "  [$($lastWrite.ToString('yyyy-MM-dd'))] $name"; $recentDumps += $lastWrite.ToString('o') }
                    }
                    $data['recentDumps'] = $recentDumps
                } else { $content += "[OK] Aucun minidump" }
            }
            catch { $data['minidumpCount'] = 0; $content += "[INFO] Acces aux minidumps non disponible" }
        } else { $data['minidumpCount'] = 0; $content += "[INFO] Dossier Minidump non trouve" }
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-MinidumpAnalysis' -Message $_.Exception.Message }
    $Script:SectionData['MinidumpAnalysis'] = $data
    return $content
}

function Collect-ReliabilityHistory {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "HISTORIQUE DE FIABILITE"; $content += ("-" * 50)
        $records = Get-WmiSafe -ClassName 'Win32_ReliabilityRecords'
        if ($null -ne $records) {
            $recentRecords = @($records) | Where-Object { $null -ne $_ -and (Get-SafePropValue $_ 'TimeGenerated') -gt (Get-Date).AddDays(-30) } | Select-Object -First 20
            $recentArray = @($recentRecords)
            $appCrashes = Get-SafeCount ($recentArray | Where-Object { (Get-SafePropValue $_ 'SourceName' '') -match 'Application' })
            $data['eventCount'] = Get-SafeCount $recentArray; $data['appCrashes'] = $appCrashes
            $content += "Evenements fiabilite (30j): $(Get-SafeDictValue $data 'eventCount' 0)"
            $content += "Crashes applications: $appCrashes"
        } else { $data['eventCount'] = 0; $data['appCrashes'] = 0; $content += "[INFO] Win32_ReliabilityRecords non disponible" }
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-ReliabilityHistory' -Message $_.Exception.Message }
    $Script:SectionData['ReliabilityHistory'] = $data
    return $content
}

function Collect-PerformanceCounters {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "COMPTEURS DE PERFORMANCE"; $content += ("-" * 50)
        try {
            $diskQueue = Get-Counter '\PhysicalDisk(_Total)\Avg. Disk Queue Length' -SampleInterval 1 -MaxSamples 1 -ErrorAction Stop
            $samples = @(Get-SafePropValue $diskQueue 'CounterSamples' @())
            if ((Get-SafeCount $samples) -gt 0) {
                $queueValue = [math]::Round((Get-SafePropValue $samples[0] 'CookedValue' 0), 2)
                $data['diskQueueLength'] = $queueValue; $content += "Disk Queue Length        : $queueValue"
            }
        } catch { $data['diskQueueLength'] = -1 }
        
        try {
            $diskRead = Get-Counter '\PhysicalDisk(_Total)\Disk Read Bytes/sec' -SampleInterval 1 -MaxSamples 1 -ErrorAction SilentlyContinue
            $samples = @(Get-SafePropValue $diskRead 'CounterSamples' @())
            if ((Get-SafeCount $samples) -gt 0) {
                $readMBs = [math]::Round((Get-SafePropValue $samples[0] 'CookedValue' 0) / 1MB, 2)
                $data['diskReadMBs'] = $readMBs; $content += "Disk Read                : $readMBs MB/s"
            }
        } catch { }
        
        try {
            $diskWrite = Get-Counter '\PhysicalDisk(_Total)\Disk Write Bytes/sec' -SampleInterval 1 -MaxSamples 1 -ErrorAction SilentlyContinue
            $samples = @(Get-SafePropValue $diskWrite 'CounterSamples' @())
            if ((Get-SafeCount $samples) -gt 0) {
                $writeMBs = [math]::Round((Get-SafePropValue $samples[0] 'CookedValue' 0) / 1MB, 2)
                $data['diskWriteMBs'] = $writeMBs; $content += "Disk Write               : $writeMBs MB/s"
            }
        } catch { }
        
        try {
            $pageFaults = Get-Counter '\Memory\Page Faults/sec' -SampleInterval 1 -MaxSamples 1 -ErrorAction SilentlyContinue
            $samples = @(Get-SafePropValue $pageFaults 'CounterSamples' @())
            if ((Get-SafeCount $samples) -gt 0) {
                $pfValue = [math]::Round((Get-SafePropValue $samples[0] 'CookedValue' 0), 0)
                $data['pageFaultsPerSec'] = $pfValue; $content += "Page Faults/sec          : $pfValue"
            }
        } catch { }
        
        try {
            $availMem = Get-Counter '\Memory\Available MBytes' -SampleInterval 1 -MaxSamples 1 -ErrorAction SilentlyContinue
            $samples = @(Get-SafePropValue $availMem 'CounterSamples' @())
            if ((Get-SafeCount $samples) -gt 0) {
                $availMB = [math]::Round((Get-SafePropValue $samples[0] 'CookedValue' 0), 0)
                $data['availableMemoryMB'] = $availMB; $content += "Available Memory         : $availMB MB"
            }
        } catch { }
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-PerformanceCounters' -Message $_.Exception.Message }
    $Script:SectionData['PerformanceCounters'] = $data
    return $content
}

function Collect-NetworkLatency {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "LATENCE RESEAU"; $content += ("-" * 50); $content += ""; $content += "PING TEST"; $content += ("-" * 50)
        $pingResults = @(); $targets = @($Script:NetworkTestTargets)
        foreach ($target in $targets) {
            try {
                if (-not $Script:AllowExternalNetworkTests) {
                    $content += "$target : [DESACTIVE]"
                    $pingResults += [ordered]@{ target = $target; latencyMs = -1; success = $false; skipped = $true }
                    continue
                }
                $ping = Test-Connection -ComputerName $target -Count 2 -ErrorAction Stop
                $pingArray = @($ping)
                $avgMs = 0
                if ((Get-SafeCount $pingArray) -gt 0) { $avgMs = [math]::Round(($pingArray | Measure-Object -Property ResponseTime -Average).Average, 1) }
                $content += "$target : $avgMs ms"
                $pingResults += [ordered]@{ target = $target; latencyMs = $avgMs; success = $true; skipped = $false }
            }
            catch { $content += "$target : [ECHEC]"; $pingResults += [ordered]@{ target = $target; latencyMs = -1; success = $false; skipped = $false } }
        }
        $data['ping'] = $pingResults
        
        $content += ""; $content += "DNS RESOLUTION"; $content += ("-" * 50)
        $dnsResults = @(); $dnsTargets = @($Script:DnsTestTargets)
        foreach ($target in $dnsTargets) {
            try {
                if (-not $Script:AllowExternalNetworkTests) {
                    $content += "$target : [DESACTIVE]"
                    $dnsResults += [ordered]@{ target = $target; resolutionMs = -1; success = $false; skipped = $true }
                    continue
                }
                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                $resolved = [System.Net.Dns]::GetHostAddresses($target)
                $sw.Stop()
                $dnsMs = $sw.ElapsedMilliseconds
                $resolvedArray = @($resolved)
                $firstIP = "N/A"
                if ((Get-SafeCount $resolvedArray) -gt 0) { $firstIP = Protect-IPAddress (Get-SafePropValue $resolvedArray[0] 'IPAddressToString' 'N/A') }
                $content += "$target : $dnsMs ms -> $firstIP"
                $dnsResults += [ordered]@{ target = $target; resolutionMs = $dnsMs; success = $true; skipped = $false }
            }
            catch { $content += "$target : [ECHEC RESOLUTION]"; $dnsResults += [ordered]@{ target = $target; resolutionMs = -1; success = $false; skipped = $false } }
        }
        $data['dns'] = $dnsResults
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-NetworkLatency' -Message $_.Exception.Message }
    $Script:SectionData['NetworkLatency'] = $data
    return $content
}

function Collect-SmartDetails {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "SMART DETAILLE"; $content += ("-" * 50)
        $smartData = @()
        $smartStatus = Get-WmiSafe -ClassName 'MSStorageDriver_FailurePredictStatus' -Namespace 'root\wmi'
        if ($null -ne $smartStatus) {
            $smartAttribs = Get-WmiSafe -ClassName 'MSStorageDriver_FailurePredictData' -Namespace 'root\wmi'
            $statusArray = @($smartStatus)
            foreach ($status in $statusArray) {
                if ($null -eq $status) { continue }
                $instanceName = Get-SafePropValue $status 'InstanceName' 'N/A'
                $predictFailure = Get-SafePropValue $status 'PredictFailure' $false
                $diskInfo = [ordered]@{ instanceName = $instanceName; predictFailure = $predictFailure }
                $content += "Disque: $($instanceName.Split('\')[-1])"
                $content += "  Prediction defaillance : $(if($predictFailure){'OUI - ALERTE!'}else{'Non'})"
                
                if ($null -ne $smartAttribs) {
                    $matchingAttrib = @($smartAttribs) | Where-Object { (Get-SafePropValue $_ 'InstanceName' '') -eq $instanceName } | Select-Object -First 1
                    if ($null -ne $matchingAttrib) {
                        $vendorSpecific = Get-SafePropValue $matchingAttrib 'VendorSpecific'
                        if ($null -ne $vendorSpecific) {
                            $rawData = @($vendorSpecific)
                            $rawLen = Get-SafeCount $rawData
                            for ($i = 2; $i -lt ($rawLen - 12); $i += 12) {
                                try {
                                    $attrId = $rawData[$i]
                                    if ($attrId -eq 0) { continue }
                                    $attrRaw = [BitConverter]::ToUInt32($rawData, $i + 5)
                                    switch ($attrId) {
                                        5 { $content += "  Reallocated Sectors    : $attrRaw"; $diskInfo['reallocatedSectors'] = $attrRaw }
                                        9 { $content += "  Power-On Hours         : $attrRaw h"; $diskInfo['powerOnHours'] = $attrRaw }
                                        187 { $content += "  Reported Uncorrectable : $attrRaw"; $diskInfo['reportedUncorrectable'] = $attrRaw }
                                        194 {
                                            $tempC = Normalize-Temperature -Value $attrRaw -Min 0 -Max 90
                                            if ($null -ne $tempC) {
                                                $content += "  Temperature            : $tempC C"
                                                $diskInfo['temperature'] = $tempC
                                            } else {
                                                $content += "  Temperature            : N/A (invalid reading)"
                                                $diskInfo['temperature'] = $null
                                                Add-ErrorLog -Type 'TEMP_WARN' -Source 'Collect-SmartDetails' -Message "Temperature SMART invalide: $attrRaw"
                                            }
                                        }
                                        196 { $content += "  Reallocation Events    : $attrRaw"; $diskInfo['reallocationEvents'] = $attrRaw }
                                        197 { $content += "  Pending Sectors        : $attrRaw"; $diskInfo['pendingSectors'] = $attrRaw }
                                        198 { $content += "  Uncorrectable Sectors  : $attrRaw"; $diskInfo['uncorrectableSectors'] = $attrRaw }
                                    }
                                } catch { }
                            }
                        }
                    }
                }
                $smartData += $diskInfo; $content += ""
            }
        } else { $content += "[INFO] Donnees SMART non disponibles" }
        $data['disks'] = $smartData; $data['diskCount'] = Get-SafeCount $smartData
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-SmartDetails' -Message $_.Exception.Message }
    $Script:SectionData['SmartDetails'] = $data
    return $content
}

function Collect-DynamicSignals {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "SIGNES DYNAMIQUES (DONNEES BRUTES)"; $content += ("=" * 50); $content += ""
        $monitorDuration = $Script:MonitorSeconds
        $sampleInterval = Get-SafeDictValue $Script:Config.DynamicSignals 'SampleInterval' 1
        $samples = [math]::Floor($monitorDuration / $sampleInterval)
        $content += "[CONFIG] Duree: ${monitorDuration}s | Echantillons: $samples"; $content += ""

        $cpuCounter = '\\Processor(_Total)\\% Processor Time'
        $memCounters = @('\\Memory\\Available MBytes','\\Memory\\Page Faults/sec')
        $diskCounters = @('\\PhysicalDisk(_Total)\\% Disk Time','\\PhysicalDisk(_Total)\\Avg. Disk Queue Length')
        $allCounters = @($cpuCounter) + $memCounters + $diskCounters

        $netStart = $null; $activeAdapter = $null
        try {
            $activeAdapter = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { (Get-SafePropValue $_ 'Status' '') -eq 'Up' -and -not (Get-SafePropValue $_ 'Virtual' $false) } | Sort-Object -Property LinkSpeed -Descending | Select-Object -First 1
            if ($null -ne $activeAdapter) {
                $adapterName = Get-SafePropValue $activeAdapter 'Name' ''
                if ($adapterName) { $netStart = Get-NetAdapterStatistics -Name $adapterName -ErrorAction SilentlyContinue }
            }
        } catch { }

        $pathValues = @{}
        $counterValid = $false
        try {
            $counterResults = Get-Counter -Counter $allCounters -SampleInterval $sampleInterval -MaxSamples $samples -ErrorAction Stop
            $counterSamples = Get-SafePropValue $counterResults 'CounterSamples' @()
            $sampleArray = @($counterSamples)
            foreach ($sample in $sampleArray) {
                if ($null -eq $sample) { continue }
                $path = Get-SafePropValue $sample 'Path' ''
                if ([string]::IsNullOrEmpty($path)) { continue }
                if (-not (Test-SafeHasKey $pathValues $path)) { $pathValues[$path] = @() }
                $pathValues[$path] += Get-SafePropValue $sample 'CookedValue' 0
            }
            if ((Get-SafeCount $sampleArray) -gt 0) { $counterValid = $true }
        } catch { Add-ErrorLog -Type 'COUNTER_ERROR' -Source 'Collect-DynamicSignals' -Message $_.Exception.Message }

        # CPU via Get-Counter
        $cpuVals = @(); if (Test-SafeHasKey $pathValues $cpuCounter) { $cpuVals = @($pathValues[$cpuCounter]) }
        $cpuCount = Get-SafeCount $cpuVals
        $cpuAvg = 0; $cpuMax = 0; $cpuMin = 0
        $cpuSource = 'Get-Counter'
        if ($cpuCount -gt 0) {
            $cpuAvg = [math]::Round(($cpuVals | Measure-Object -Average).Average, 2)
            $cpuMax = [math]::Round(($cpuVals | Measure-Object -Maximum).Maximum, 2)
            $cpuMin = [math]::Round(($cpuVals | Measure-Object -Minimum).Minimum, 2)
        }
        
        # Validation CPU: si 0 samples ou cpuAvg=0 ET cpuMax=0 -> fallback WMI
        if ($cpuCount -eq 0 -or ($cpuAvg -eq 0 -and $cpuMax -eq 0)) {
            try {
                $wmiCpu = Get-CimInstance -ClassName 'Win32_PerfFormattedData_PerfOS_Processor' -Filter "Name='_Total'" -ErrorAction Stop
                if ($null -ne $wmiCpu) {
                    $cpuAvg = [math]::Round((Get-SafePropValue $wmiCpu 'PercentProcessorTime' 0), 2)
                    $cpuMax = $cpuAvg; $cpuMin = $cpuAvg; $cpuCount = 1
                    $cpuSource = 'WMI-Fallback'
                }
            } catch { }
        }
        $data['cpu'] = [ordered]@{ average = $cpuAvg; max = $cpuMax; min = $cpuMin; sampleCount = $cpuCount; source = $cpuSource }
        $content += "CPU: moyenne $cpuAvg% | max $cpuMax% | min $cpuMin% [$cpuSource]"

        # Memoire via Get-Counter
        $memKey = '\\Memory\\Available MBytes'
        $availVals = @(); if (Test-SafeHasKey $pathValues $memKey) { $availVals = @($pathValues[$memKey]) }
        $availMB = 0; if ((Get-SafeCount $availVals) -gt 0) { $availMB = [math]::Round(($availVals | Measure-Object -Average).Average, 2) }
        
        $pfKey = '\\Memory\\Page Faults/sec'
        $pfVals = @(); if (Test-SafeHasKey $pathValues $pfKey) { $pfVals = @($pathValues[$pfKey]) }
        $pfAvg = 0; if ((Get-SafeCount $pfVals) -gt 0) { $pfAvg = [math]::Round(($pfVals | Measure-Object -Average).Average, 2) }
        
        $totalMem = 0
        $memSource = 'Get-Counter'
        try {
            $osInfo = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction SilentlyContinue
            if ($null -ne $osInfo) { $totalMem = [math]::Round((Get-SafePropValue $osInfo 'TotalVisibleMemorySize' 0) / 1KB, 2) }
        } catch { }
        
        # Validation memoire: si availMB=0 et totalMem>0 -> fallback WMI
        if ($availMB -eq 0 -and $totalMem -gt 0) {
            try {
                $freeKB = Get-SafePropValue $osInfo 'FreePhysicalMemory' 0
                $availMB = [math]::Round($freeKB / 1KB, 2)
                $memSource = 'WMI-Fallback'
            } catch { }
        }
        
        $usedPercent = 0
        if ($totalMem -gt 0) { $usedPercent = [math]::Round((($totalMem - $availMB) / $totalMem) * 100, 2) }
        $data['memory'] = [ordered]@{ usedPercent = $usedPercent; availableMB = $availMB; pageFaultsPerSec = $pfAvg; sampleCount = (Get-SafeCount $availVals); source = $memSource }
        $content += "Memoire: $usedPercent% utilisee | $availMB MB libres [$memSource]"

        # Disque via Get-Counter
        $diskTimeKey = '\\PhysicalDisk(_Total)\\% Disk Time'
        $diskTimeVals = @(); if (Test-SafeHasKey $pathValues $diskTimeKey) { $diskTimeVals = @($pathValues[$diskTimeKey]) }
        $diskTimeAvg = 0; if ((Get-SafeCount $diskTimeVals) -gt 0) { $diskTimeAvg = [math]::Round(($diskTimeVals | Measure-Object -Average).Average, 2) }
        
        $diskQueueKey = '\\PhysicalDisk(_Total)\\Avg. Disk Queue Length'
        $diskQueueVals = @(); if (Test-SafeHasKey $pathValues $diskQueueKey) { $diskQueueVals = @($pathValues[$diskQueueKey]) }
        $diskQueueAvg = 0; if ((Get-SafeCount $diskQueueVals) -gt 0) { $diskQueueAvg = [math]::Round(($diskQueueVals | Measure-Object -Average).Average, 2) }
        
        $diskSource = 'Get-Counter'
        # Fallback disque si pas de samples
        if ((Get-SafeCount $diskTimeVals) -eq 0) {
            try {
                $wmiDisk = Get-CimInstance -ClassName 'Win32_PerfFormattedData_PerfDisk_PhysicalDisk' -Filter "Name='_Total'" -ErrorAction Stop
                if ($null -ne $wmiDisk) {
                    $diskTimeAvg = [math]::Round((Get-SafePropValue $wmiDisk 'PercentDiskTime' 0), 2)
                    $diskQueueAvg = [math]::Round((Get-SafePropValue $wmiDisk 'AvgDiskQueueLength' 0), 2)
                    $diskSource = 'WMI-Fallback'
                }
            } catch { }
        }
        $data['disk'] = [ordered]@{ diskTimePercent = $diskTimeAvg; queueLength = $diskQueueAvg; sampleCount = (Get-SafeCount $diskTimeVals); source = $diskSource }
        $content += "Disque: utilisation $diskTimeAvg% | file $diskQueueAvg [$diskSource]"

        $topCpu = @(); $topMemory = @()
        try {
            # Tri sûr des processus par consommation CPU (en secondes) via Get-ProcCpuSeconds
            $procs = Get-Process -ErrorAction SilentlyContinue |
                Sort-Object @{ Expression = { Get-ProcCpuSeconds $_ } ; Descending = $true } |
                Select-Object -First 5
            $procArray = @($procs)
            foreach ($p in $procArray) {
                if ($null -eq $p) { continue }
                $topCpu += [ordered]@{
                    name       = (Get-SafePropValue $p 'ProcessName' 'N/A');
                    cpuSeconds = (Get-ProcCpuSeconds $p)
                }
            }
            # Tri des processus par consommation mémoire (WorkingSet64) inchangé
            $procsM = Get-Process -ErrorAction SilentlyContinue |
                Sort-Object WorkingSet64 -Descending | Select-Object -First 5
            $procMArray = @($procsM)
            foreach ($p in $procMArray) {
                if ($null -eq $p) { continue }
                $ws = Get-SafePropValue $p 'WorkingSet64' 0
                $topMemory += [ordered]@{
                    name     = (Get-SafePropValue $p 'ProcessName' 'N/A');
                    memoryMB = [math]::Round((Convert-SafeLong $ws) / 1MB, 2)
                }
            }
        } catch { }
        $data['topCpu'] = $topCpu; $data['topMemory'] = $topMemory
        if ((Get-SafeCount $topCpu) -gt 0) { $content += "Top CPU: $(Get-SafeDictValue $topCpu[0] 'name' 'N/A')" }
        if ((Get-SafeCount $topMemory) -gt 0) { $content += "Top RAM: $(Get-SafeDictValue $topMemory[0] 'name' 'N/A') ($(Get-SafeDictValue $topMemory[0] 'memoryMB' 0) MB)" }

        $throughputMbps = 0; $netErrors = 0
        try {
            if ($null -ne $activeAdapter -and $null -ne $netStart) {
                $adapterName = Get-SafePropValue $activeAdapter 'Name' ''
                $netEnd = Get-NetAdapterStatistics -Name $adapterName -ErrorAction SilentlyContinue
                if ($null -ne $netEnd) {
                    $bytesIn = (Get-SafePropValue $netEnd 'ReceivedBytes' 0) - (Get-SafePropValue $netStart 'ReceivedBytes' 0)
                    $bytesOut = (Get-SafePropValue $netEnd 'SentBytes' 0) - (Get-SafePropValue $netStart 'SentBytes' 0)
                    $elapsed = $samples * $sampleInterval
                    if ($elapsed -gt 0) { $throughputMbps = [math]::Round((((Convert-SafeLong $bytesIn) + (Convert-SafeLong $bytesOut)) * 8) / ($elapsed * 1MB), 2) }
                    $errStart = (Get-SafePropValue $netStart 'InboundPacketsWithErrors' 0) + (Get-SafePropValue $netStart 'OutboundPacketsWithErrors' 0)
                    $errEnd = (Get-SafePropValue $netEnd 'InboundPacketsWithErrors' 0) + (Get-SafePropValue $netEnd 'OutboundPacketsWithErrors' 0)
                    $netErrors = $errEnd - $errStart
                }
            }
        } catch { }
        $adapterName = "inconnu"
        if ($null -ne $activeAdapter) { $adapterName = Get-SafePropValue $activeAdapter 'Name' 'inconnu' }
        $data['network'] = [ordered]@{ adapter = $adapterName; throughputMbps = $throughputMbps; errors = $netErrors }
        $content += "Reseau: $throughputMbps Mbps | erreurs: $netErrors"
        $data['monitorDuration'] = $monitorDuration; $data['samplesCollected'] = $samples
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-DynamicSignals' -Message $_.Exception.Message }
    $Script:SectionData['DynamicSignals'] = $data
    return $content
}

function Collect-AdvancedAnalysis {
    $content = @(); $data = [ordered]@{}
    try {
        $content += "ANALYSE AVANCEE (DONNEES BRUTES)"; $content += ("=" * 50)
        
        $content += ""; $content += "FRAGMENTATION DISQUE"; $content += ("-" * 50)
        $fragData = [ordered]@{}
        if ($Script:IsAdmin) {
            try {
                $volumes = Get-Volume -ErrorAction SilentlyContinue | Where-Object { (Get-SafePropValue $_ 'DriveType' '') -eq 'Fixed' -and (Get-SafePropValue $_ 'DriveLetter') }
                $volumeArray = @($volumes)
                foreach ($vol in $volumeArray) {
                    if ($null -eq $vol) { continue }
                    $driveLetter = Get-SafePropValue $vol 'DriveLetter' ''
                    if ([string]::IsNullOrEmpty($driveLetter)) { continue }
                    try {
                        $result = Optimize-Volume -DriveLetter $driveLetter -Analyze -Verbose 4>&1 -ErrorAction Stop
                        $fragPercent = -1
                        $resultArray = @($result)
                        foreach ($line in $resultArray) {
                            $lineStr = "$line"
                            if ($lineStr -match '(\d+)%') { try { $fragPercent = [int]$Matches[1] } catch { $fragPercent = -1 }; break }
                        }
                        $fragData[$driveLetter] = $fragPercent
                        $content += "${driveLetter}: $fragPercent%"
                    } catch { $fragData[$driveLetter] = -1; $content += "${driveLetter}: [Non disponible]" }
                }
            } catch { $content += "[INFO] Analyse fragmentation non disponible" }
        } else { $content += "[INFO] Admin requis" }
        $data['fragmentation'] = $fragData

        $content += ""; $content += "BENCHMARKS SYSTEME"; $content += ("-" * 50)
        $benchData = [ordered]@{}
        try {
            $winsatPath = "$env:SystemRoot\Performance\WinSAT\DataStore"
            if (Test-Path $winsatPath) {
                $winsatFiles = Get-ChildItem -Path $winsatPath -Filter "*.xml" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if ($null -ne $winsatFiles) {
                    try {
                        $fullName = Get-SafePropValue $winsatFiles 'FullName' ''
                        if (-not [string]::IsNullOrEmpty($fullName) -and (Test-Path $fullName -ErrorAction SilentlyContinue)) {
                            [xml]$winsatXml = Get-Content $fullName -ErrorAction Stop
                            $winSPR = $null
                            if ($null -ne $winsatXml) {
                                try { $winSPR = $winsatXml.SelectSingleNode('//WinSPR') } catch { $winSPR = $null }
                            }
                            if ($null -ne $winSPR) {
                                try { $benchData['cpu'] = [double]($winSPR.SelectSingleNode('CpuScore').'#text') } catch { $benchData['cpu'] = -1 }
                                try { $benchData['memory'] = [double]($winSPR.SelectSingleNode('MemoryScore').'#text') } catch { $benchData['memory'] = -1 }
                                try { $benchData['disk'] = [double]($winSPR.SelectSingleNode('DiskScore').'#text') } catch { $benchData['disk'] = -1 }
                                try { $benchData['graphics'] = [double]($winSPR.SelectSingleNode('GraphicsScore').'#text') } catch { $benchData['graphics'] = -1 }
                                $lastWrite = Get-SafePropValue $winsatFiles 'LastWriteTime'
                                $lastRunValue = 'Unknown'
                                if ($null -ne $lastWrite) { 
                                    try { $lastRunValue = $lastWrite.ToString('o') } catch { $lastRunValue = 'Unknown' } 
                                }
                                $benchData['lastRun'] = $lastRunValue
                                $content += "CPU Score: $(Get-SafeDictValue $benchData 'cpu' -1) | Memory: $(Get-SafeDictValue $benchData 'memory' -1) | Disk: $(Get-SafeDictValue $benchData 'disk' -1) | Graphics: $(Get-SafeDictValue $benchData 'graphics' -1)"
                            } else { $content += "[INFO] Donnees WinSAT invalides" }
                        } else { $content += "[INFO] Fichier WinSAT introuvable" }
                    } catch { $content += "[INFO] Erreur lecture WinSAT: $($_.Exception.Message)" }
                } else { $content += "[INFO] Aucun benchmark WinSAT" }
            } else { $content += "[INFO] WinSAT non disponible" }
        } catch { $content += "[INFO] Lecture benchmarks impossible" }
        $data['benchmarks'] = $benchData

        $content += ""; $content += "DETECTION MALWARE"; $content += ("-" * 50)
        $malwareData = [ordered]@{}
        try {
            $threats = Get-MpThreat -ErrorAction Stop
            $threatArray = @($threats)
            $threatCount = Get-SafeCount $threatArray
            if ($threatCount -gt 0) {
                $threatList = @()
                foreach ($threat in $threatArray) {
                    if ($null -eq $threat) { continue }
                    $threatName = Get-SafePropValue $threat 'ThreatName' 'N/A'
                    $severity = Get-SafePropValue $threat 'SeverityID' 0
                    $content += "[MENACE] $threatName - Severite: $severity"
                    $threatList += [ordered]@{ name = $threatName; severity = $severity }
                }
                $malwareData['threats'] = $threatList; $malwareData['count'] = $threatCount
            } else { $content += "[OK] Aucune menace active"; $malwareData['count'] = 0 }
        } catch { $content += "[INFO] Get-MpThreat non disponible"; $malwareData['count'] = -1 }
        $data['malware'] = $malwareData
    }
    catch { Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'Collect-AdvancedAnalysis' -Message $_.Exception.Message }
    $Script:SectionData['AdvancedAnalysis'] = $data
    return $content
}
#endregion

#region ============== RAPPORT ==============
function Generate-ErrorReport {
    $content = @()
    $errorCount = Get-SafeCount $Script:ErrorLog
    if ($errorCount -eq 0) { return $null }
    $content += "ERREURS DE COLLECTE"; $content += ("=" * 50); $content += "Total erreurs: $errorCount"; $content += ""
    $errorArray = @($Script:ErrorLog)
    foreach ($err in $errorArray) {
        if ($null -eq $err) { continue }
        $type = Get-SafePropValue $err 'Type' 'UNKNOWN'
        $source = Get-SafePropValue $err 'Source' 'Unknown'
        $msg = Get-SafePropValue $err 'Message' 'No message'
        $content += "[$type] $source"; $content += "  $msg"; $content += ""
    }
    return $content
}

function Generate-Summary {
    $content = @()
    try {
        $content += "RESUME DE COLLECTE"; $content += ("=" * 50); $content += ""
        $content += "METADATA"; $content += ("-" * 50)
        $content += "Version              : $($Script:ScriptVersion)"
        $content += "Run ID               : $($Script:RunId)"
        $content += "Date                 : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
        $content += "Admin                : $($Script:IsAdmin)"
        $content += "Redaction            : $(if(-not $Script:NoRedact){$Script:RedactLevel}else{'DESACTIVEE'})"
        $content += "QuickScan            : $($Script:QuickScan)"
        $content += "MonitorSeconds       : $($Script:MonitorSeconds)"; $content += ""
        $content += "SECTIONS COLLECTEES"; $content += ("-" * 50)
        $sectionKeys = @()
        if ($null -ne $Script:SectionData -and $Script:SectionData -is [System.Collections.IDictionary]) {
            $sectionKeys = @($Script:SectionData.Keys)
        }
        $content += "Sections: $(Get-SafeCount $sectionKeys)"
        foreach ($key in $sectionKeys) { $content += "  - $key" }
        $content += ""; $content += "ERREURS"; $content += ("-" * 50)
        $content += "Erreurs de collecte: $(Get-SafeCount $Script:ErrorLog)"
        $Script:SectionData['_metadata'] = [ordered]@{
            schemaVersion = Get-SafeDictValue $Script:Config 'SchemaVersion' '6.6.0'
            scriptVersion = $Script:ScriptVersion; runId = $Script:RunId
            timestamp = (Get-Date).ToString('o'); isAdmin = $Script:IsAdmin
            redactionEnabled = (-not $Script:NoRedact); redactLevel = $Script:RedactLevel
            quickScan = $Script:QuickScan; monitorSeconds = $Script:MonitorSeconds
            sectionCount = Get-SafeCount $sectionKeys; errorCount = Get-SafeCount $Script:ErrorLog
            redactionCount = Get-SafeDictValue $Script:RedactionStats 'TotalRedactions' 0
            allowExternalNetworkTests = $Script:AllowExternalNetworkTests
            externalCommandTimeoutSeconds = $Script:ExternalCommandTimeoutSeconds
            maxTempFiles = $Script:MaxTempFiles
        }
        $Script:SectionData['_errors'] = Get-ErrorLogData
        $Script:SectionData['_collectorStatus'] = $Script:CollectorStatus
    }
    catch { $content += "[ERREUR] Generation resume: $($_.Exception.Message)" }
    return $content
}
#endregion

#region ============== EXECUTION PRINCIPALE ==============
try {
    Write-Host "COLLECTE DIAGNOSTIC PC v$($Script:ScriptVersion) - Run ID: $($Script:RunId)" -ForegroundColor Cyan
    Write-Host "Sortie: $($Script:OutputPath)"
    Write-Host "[MODE] Collecte pure - Analyse par IA externe" -ForegroundColor Yellow
    if (-not $Script:NoRedact) { Write-Host "[REDACTION] Niveau: $($Script:RedactLevel)" -ForegroundColor Cyan }
    Write-Host "[MONITORING] $($Script:MonitorSeconds) secondes" -ForegroundColor Cyan

    $preflightOK = Invoke-PreflightCheck -ScriptPath $PSCommandPath -SkipCheck:$SkipPreflightCheck
    if (-not $preflightOK) {
        Write-Host "[WARN] Execution Policy non valide - collecte degradee" -ForegroundColor Yellow
        Add-ErrorLog -Type 'PREFLIGHT_ERROR' -Source 'Preflight' -Message "Execution Policy non valide"
    }

    $Script:IsAdmin = Test-Administrator
    if ($Script:IsAdmin) { Write-Host "[OK] Mode Administrateur" -ForegroundColor Green }
    else { Write-Host "[!] Mode utilisateur standard" -ForegroundColor Yellow }

    Write-ReportLine ("#" * 100)
    Write-ReportLine "#" + (" " * 15) + "RAPPORT DE COLLECTE DIAGNOSTIC PC v$($Script:ScriptVersion) - DONNEES BRUTES" + (" " * 15) + "#"
    Write-ReportLine ("#" * 100); Write-ReportLine ""
    Write-ReportLine "Ce rapport contient des DONNEES BRUTES uniquement."
    Write-ReportLine "L'analyse et l'interpretation sont effectuees par l'IA externe."; Write-ReportLine ""
    Write-ReportLine "METADATA"; Write-ReportLine ("-" * 50)
    Write-ReportLine "Run ID               : $($Script:RunId)"
    Write-ReportLine "Version              : $($Script:ScriptVersion)"
    Write-ReportLine "Date                 : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    Write-ReportLine "Machine              : $(Protect-ComputerName $env:COMPUTERNAME)"
    Write-ReportLine "Admin                : $Script:IsAdmin"
    Write-ReportLine "Redaction            : $(if(-not $Script:NoRedact){$Script:RedactLevel}else{'DESACTIVEE'})"
    Write-ReportLine "MonitorSeconds       : $($Script:MonitorSeconds)"
    Write-ReportLine "ExternalNetTests     : $($Script:AllowExternalNetworkTests)"
    Write-ReportLine ""

    $collectors = @(
        @{Name='Identite Machine'; Function={Collect-MachineIdentity}; Section='1'}
        @{Name='Systeme Exploitation'; Function={Collect-OSInfo}; Section='2'}
        @{Name='Processeur'; Function={Collect-CPU}; Section='3'}
        @{Name='Memoire'; Function={Collect-Memory}; Section='4'}
        @{Name='Stockage'; Function={Collect-Storage}; Section='5'}
        @{Name='Carte Graphique'; Function={Collect-GPU}; Section='6'}
        @{Name='Reseau'; Function={Collect-Network}; Section='7'}
        @{Name='Securite'; Function={Collect-Security}; Section='8'}
        @{Name='Services'; Function={Collect-Services}; Section='9'}
        @{Name='Demarrage'; Function={Collect-StartupPrograms}; Section='10'}
        @{Name='Health Checks'; Function={Collect-HealthChecks}; Section='11'}
        @{Name='Journaux Evenements'; Function={Collect-EventLogs}; Section='12'}
        @{Name='Windows Update'; Function={Collect-WindowsUpdate}; Section='13'}
        @{Name='Audio'; Function={Collect-Audio}; Section='14'}
        @{Name='Peripheriques'; Function={Collect-DevicesDrivers}; Section='15'}
        @{Name='Applications'; Function={Collect-InstalledApplications}; Section='16'}
        @{Name='Taches Planifiees'; Function={Collect-ScheduledTasks}; Section='17'}
        @{Name='Processus'; Function={Collect-Processes}; Section='18'}
        @{Name='Batterie'; Function={Collect-Battery}; Section='19'}
        @{Name='Imprimantes'; Function={Collect-Printers}; Section='20'}
        @{Name='Profils Utilisateurs'; Function={Collect-UserProfiles}; Section='21'}
        @{Name='Virtualisation'; Function={Collect-Virtualization}; Section='22'}
        @{Name='Points Restauration'; Function={Collect-RestorePoints}; Section='23'}
        @{Name='Fichiers Temporaires'; Function={Collect-TempFiles}; Section='24'}
        @{Name='Variables Environnement'; Function={Collect-EnvironmentVariables}; Section='25'}
        @{Name='Certificats'; Function={Collect-Certificates}; Section='26'}
        @{Name='Registre'; Function={Collect-RegistryKeys}; Section='27'}
        @{Name='Temperatures'; Function={Collect-Temperatures}; Section='28'}
        @{Name='Integrite Systeme'; Function={Collect-SystemIntegrity}; Section='29'}
        @{Name='Alimentation'; Function={Collect-PowerSettings}; Section='30'}
        @{Name='Analyse Minidumps'; Function={Collect-MinidumpAnalysis}; Section='31'}
        @{Name='Historique Fiabilite'; Function={Collect-ReliabilityHistory}; Section='32'}
        @{Name='Compteurs Performance'; Function={Collect-PerformanceCounters}; Section='33'}
        @{Name='Latence Reseau'; Function={Collect-NetworkLatency}; Section='34'}
        @{Name='SMART Detaille'; Function={Collect-SmartDetails}; Section='35'}
    )

    $total = Get-SafeCount $collectors; $current = 0
    foreach ($collector in $collectors) {
        $current++
        $collectorName = Get-SafeDictValue $collector 'Name' 'Unknown'
        $collectorSection = Get-SafeDictValue $collector 'Section' '?'
        $collectorFunc = Get-SafeDictValue $collector 'Function'
        Show-Progress -current $current -total $total -section $collectorName
        try {
            if ($null -ne $collectorFunc) {
                $sectionContent = & $collectorFunc
                Write-Section -Title "$collectorSection. $($collectorName.ToUpper())" -Content $sectionContent
                Set-CollectorStatus -Name $collectorName -Status 'ok'
            }
        } catch {
            Write-Section -Title "$collectorSection. $($collectorName.ToUpper())" -Content @("[ERREUR] $($_.Exception.Message)")
            Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source $collectorName -Message $_.Exception.Message
            Set-CollectorStatus -Name $collectorName -Status 'failed' -Message $_.Exception.Message
        }
    }

    Write-Host "`r[Monitoring dynamique ($($Script:MonitorSeconds)s)...]".PadRight(100) -NoNewline -ForegroundColor Cyan
    try { $dynContent = Collect-DynamicSignals; Write-Section -Title "36. SIGNES DYNAMIQUES" -Content $dynContent; Set-CollectorStatus -Name 'DynamicSignals' -Status 'ok' }
    catch { Write-Section -Title "36. SIGNES DYNAMIQUES" -Content @("[ERREUR] $($_.Exception.Message)"); Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'DynamicSignals' -Message $_.Exception.Message; Set-CollectorStatus -Name 'DynamicSignals' -Status 'failed' -Message $_.Exception.Message }

    Write-Host "`r[Analyse avancee...]".PadRight(100) -NoNewline -ForegroundColor Cyan
    try { $advContent = Collect-AdvancedAnalysis; Write-Section -Title "37. ANALYSE AVANCEE" -Content $advContent; Set-CollectorStatus -Name 'AdvancedAnalysis' -Status 'ok' }
    catch { Write-Section -Title "37. ANALYSE AVANCEE" -Content @("[ERREUR] $($_.Exception.Message)"); Add-ErrorLog -Type 'COLLECTOR_ERROR' -Source 'AdvancedAnalysis' -Message $_.Exception.Message; Set-CollectorStatus -Name 'AdvancedAnalysis' -Status 'failed' -Message $_.Exception.Message }

    $errorContent = Generate-ErrorReport
    if ($null -ne $errorContent) { Write-Section -Title "38. ERREURS DE COLLECTE" -Content $errorContent }

    Write-Host "`r[Generation resume...]".PadRight(100)
    $summaryContent = Generate-Summary
    Write-Section -Title "RESUME FINAL" -Content $summaryContent

    $Script:EndTime = Get-Date
    $Script:GlobalStopwatch.Stop()
    $durationSeconds = [math]::Round($Script:GlobalStopwatch.Elapsed.TotalSeconds, 1)
    Write-ReportLine ""; Write-ReportLine ("#" * 100)
    Write-ReportLine "Duree collecte       : $durationSeconds secondes"
    Write-ReportLine "Genere le            : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

    if ($Full) {
        Write-ReportLine ""; Write-ReportLine "[RAW JSON DATA]"
        try {
            $jsonOutput = (ConvertTo-JsonSafeObject (Build-JsonSnapshot)) | ConvertTo-Json -Depth 10 -Compress
            Write-ReportLine $jsonOutput
        }
        catch { Write-ReportLine "[ERREUR JSON] $($_.Exception.Message)" }
    }

    try {
        $reportContent = $Script:ReportLines -join "`n"
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        $hashBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($reportContent))
        $hashString = [BitConverter]::ToString($hashBytes) -replace '-', ''; $sha256.Dispose()
        Write-ReportLine ""; Write-ReportLine "Hash (SHA256): $hashString"
    } catch { }

    Write-Host ""
    try {
        $reportArray = @($Script:ReportLines)
        [System.IO.File]::WriteAllLines($Script:OutputPath, $reportArray, (New-Object System.Text.UTF8Encoding($true)))
        Try-HardenOutputAcl -Path $Script:OutputDir
        Write-Host "[OK] Rapport: $($Script:OutputPath)" -ForegroundColor Green
        $Script:ReportWritten = $true
        try {
            $jsonString = (ConvertTo-JsonSafeObject (Build-JsonSnapshot)) | ConvertTo-Json -Depth 10 -Compress
            [System.IO.File]::WriteAllText($Script:JsonOutputPath, $jsonString, (New-Object System.Text.UTF8Encoding($true)))
            Try-HardenOutputAcl -Path $Script:OutputDir
            Write-Host "[OK] JSON: $($Script:JsonOutputPath)" -ForegroundColor Green
            $Script:JsonWritten = $true
        } catch {
            Write-Host "[ERREUR JSON] $($_.Exception.Message)" -ForegroundColor Red
        }
    } catch {
        Write-Host "[ERREUR] Impossible d'ecrire: $($_.Exception.Message)" -ForegroundColor Red
    }

    Write-Host ""; Write-Host "Duree: ${durationSeconds}s" -ForegroundColor Cyan
    $sectionKeys = @()
    if ($null -ne $Script:SectionData -and $Script:SectionData -is [System.Collections.IDictionary]) { $sectionKeys = @($Script:SectionData.Keys) }
    Write-Host "Sections collectees: $(Get-SafeCount $sectionKeys)"
    $errorCount = Get-SafeCount $Script:ErrorLog
    if ($errorCount -gt 0) { Write-Host "Erreurs de collecte: $errorCount" -ForegroundColor Yellow }
    Write-Host ""; Write-Host "[INFO] Donnees brutes pretes pour analyse par IA externe" -ForegroundColor Green
}
catch {
    Write-Host "[ECHEC CRITIQUE] $($_.Exception.Message)" -ForegroundColor Red
    Add-ErrorLog -Type 'FATAL' -Source 'Execution' -Message $_.Exception.Message
    $Script:PartialFailure = $true
}
finally {
    if ($null -eq $Script:EndTime) { $Script:EndTime = Get-Date }

    if (-not $Script:ReportWritten) {
        try {
            $reportArray = Build-MinimalReportLines
            [System.IO.File]::WriteAllLines($Script:OutputPath, $reportArray, (New-Object System.Text.UTF8Encoding($true)))
            Try-HardenOutputAcl -Path $Script:OutputDir
            $Script:ReportWritten = $true
        } catch {
            try {
                $fallbackDir = Join-Path $env:TEMP 'VirtualITPro\Rapport'
                if (-not (Test-Path $fallbackDir)) { $null = New-Item -ItemType Directory -Path $fallbackDir -Force -ErrorAction SilentlyContinue }
                $fallbackReport = Join-Path $fallbackDir "Scan_$($Script:RunId).txt"
                $reportArray = Build-MinimalReportLines
                [System.IO.File]::WriteAllLines($fallbackReport, $reportArray, (New-Object System.Text.UTF8Encoding($true)))
                $Script:ReportWritten = $true
                $Script:OutputDir = $fallbackDir
                $Script:OutputPath = $fallbackReport
            } catch { }
        }
    }

    if (-not $Script:JsonWritten) {
        try {
            $jsonString = (ConvertTo-JsonSafeObject (Build-JsonSnapshot)) | ConvertTo-Json -Depth 10 -Compress
            [System.IO.File]::WriteAllText($Script:JsonOutputPath, $jsonString, (New-Object System.Text.UTF8Encoding($true)))
            Try-HardenOutputAcl -Path $Script:OutputDir
            $Script:JsonWritten = $true
        } catch {
            try {
                $fallbackDir = Join-Path $env:TEMP 'VirtualITPro\Rapport'
                if (-not (Test-Path $fallbackDir)) { $null = New-Item -ItemType Directory -Path $fallbackDir -Force -ErrorAction SilentlyContinue }
                $fallbackJson = Join-Path $fallbackDir "Scan_$($Script:RunId).json"
                $jsonString = (ConvertTo-JsonSafeObject (Build-JsonSnapshot)) | ConvertTo-Json -Depth 10 -Compress
                [System.IO.File]::WriteAllText($fallbackJson, $jsonString, (New-Object System.Text.UTF8Encoding($true)))
                $Script:JsonWritten = $true
                $Script:OutputDir = $fallbackDir
                $Script:JsonOutputPath = $fallbackJson
            } catch { }
        }
    }
}
#endregion
