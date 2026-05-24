#requires -Version 7.0

param(
    [switch]$ValidateOnly,
    [switch]$CheckFreshness,
    [ValidateRange(1, 100)]
    [int]$PageSize = 100,
    [ValidateRange(1, 32)]
    [int]$DetailThrottle = 8,
    [ValidateRange(5, 120)]
    [int]$TimeoutSec = 30,
    [ValidateRange(0, 10)]
    [int]$HttpRetryCount = 3,
    [ValidateRange(1, 30)]
    [int]$HttpRetryBaseDelaySec = 2
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptRoot
$DataPath = Join-Path $RepoRoot "Resources\Data\elves.json"
$SkillDataPath = Join-Path $RepoRoot "Resources\Data\skills.json"
$ElfApiUrl = "https://wikiroco.com/api/pokemon"

$ElementMap = @{
    "水" = "water"; "火" = "fire"; "草" = "grass"; "电" = "electric"; "冰" = "ice"; "武" = "fighting";
    "毒" = "poison"; "地" = "ground"; "翼" = "flying"; "虫" = "bug"; "幽" = "ghost"; "龙" = "dragon";
    "恶" = "dark"; "机械" = "steel"; "萌" = "fairy"; "普通" = "normal"; "光" = "light"; "幻" = "illusion"
}

$CategoryMap = @{
    "物攻" = "physical"
    "魔攻" = "special"
    "状态" = "status"
    "防御" = "defense"
}

$SkillSourceMap = @{
    "原生技能" = "native"
    "血脉技能" = "bloodline"
    "技能石技能" = "skill_stone"
}

function Get-JsonProperty($Object, [string]$Name, $Default = $null) {
    if ($null -eq $Object) {
        return $Default
    }

    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) {
            return $Object[$Name]
        }

        return $Default
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $Default
    }

    return $property.Value
}

function Get-Sha256Hex([string]$Text) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return -join ($hash | ForEach-Object { $_.ToString("x2") })
}

function Normalize-WikirocoText([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) {
        return ""
    }

    $value = [System.Net.WebUtility]::HtmlDecode($Text)
    $value = $value -replace '<[^>]+>', ''
    $value = $value -replace '[\u200B\u200C\u200D\uFEFF]', ''
    $value = $value -replace '[\u0009-\u000D\u0020\u00A0]+', ' '
    return $value.Trim()
}

function ConvertTo-UtcTimestampString($Value) {
    if ($Value -is [DateTime]) {
        return $Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", [Globalization.CultureInfo]::InvariantCulture)
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return ""
    }

    try {
        return ([DateTimeOffset]::Parse($text, [Globalization.CultureInfo]::InvariantCulture)).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", [Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        return $text
    }
}

function Assert-JsonProperties($Object, [string[]]$AllowedProperties, [string]$Context) {
    if ($null -eq $Object) {
        return
    }

    $allowed = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($property in $AllowedProperties) {
        [void]$allowed.Add($property)
    }

    foreach ($property in $Object.PSObject.Properties.Name) {
        if (!$allowed.Contains($property)) {
            throw "Unexpected Wikiroco field '$property' in $Context."
        }
    }
}

function ConvertTo-NullableInt($Value) {
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        return $null
    }

    return [int]$Value
}

function Get-ElfId([int]$WikirocoId) {
    return "wikiroco-elf-{0:D4}" -f $WikirocoId
}

function Get-EvolutionChainId($SourceChainId, $Stages) {
    if ($null -ne $SourceChainId) {
        return "wikiroco-evolution-chain-{0:D4}" -f [int]$SourceChainId
    }

    $fingerprint = [ordered]@{
        stages = $Stages
    }
    return "wikiroco-evolution-chain-" + (Get-Sha256Hex ($fingerprint | ConvertTo-Json -Depth 50 -Compress)).Substring(0, 12)
}

function Get-SkillBundleIndex {
    if (!(Test-Path $SkillDataPath)) {
        throw "Bundled skill database is missing: $SkillDataPath"
    }

    $data = Get-Content -LiteralPath $SkillDataPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int](Get-JsonProperty $data "schema_version" 0) -ne 2) {
        throw "Bundled skill database must use schema_version 2."
    }

    $source = Get-JsonProperty $data "source"
    $datasetHash = [string](Get-JsonProperty $source "dataset_hash" "")
    if ($datasetHash -notmatch "^sha256:[0-9a-f]{64}$") {
        throw "Bundled skill database source.dataset_hash is invalid."
    }

    $byName = @{}
    $byId = @{}
    $ids = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($skill in @($data.skills)) {
        $id = [string](Get-JsonProperty $skill "id" "")
        $nameZh = Normalize-WikirocoText ([string](Get-JsonProperty $skill "name_zh" ""))
        if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($nameZh)) {
            throw "Bundled skill database contains a skill with a missing id or name."
        }

        if ($byName.ContainsKey($nameZh)) {
            throw "Bundled skill database contains duplicate Chinese skill name: $nameZh"
        }

        $byName[$nameZh] = $id
        $byId[$id] = $skill
        [void]$ids.Add($id)
    }

    return [pscustomobject]@{
        ByName = $byName
        ById = $byId
        Ids = $ids
        Count = @($data.skills).Count
        DatasetHash = $datasetHash
        FetchedAt = ConvertTo-UtcTimestampString (Get-JsonProperty $source "fetched_at" "")
    }
}

function Convert-Attribute($Source, [string]$Context) {
    Assert-JsonProperties $Source @("attr_name", "attr_image") $Context

    $nameZh = Normalize-WikirocoText ([string](Get-JsonProperty $Source "attr_name" ""))
    if ([string]::IsNullOrWhiteSpace($nameZh)) {
        throw "Missing attribute name in $Context."
    }

    if (!$ElementMap.ContainsKey($nameZh)) {
        throw "Unknown attribute '$nameZh' in $Context."
    }

    return [ordered]@{
        element = $ElementMap[$nameZh]
        name_zh = $nameZh
        image_url = [string](Get-JsonProperty $Source "attr_image" "")
    }
}

function Convert-Attributes($Values, [string]$Context) {
    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($value in @($Values)) {
        $items.Add((Convert-Attribute $value $Context)) | Out-Null
    }

    return @($items)
}

function Convert-ElementReferenceList($Values, [string]$Context) {
    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($value in @($Values)) {
        $nameZh = Normalize-WikirocoText ([string]$value)
        if ([string]::IsNullOrWhiteSpace($nameZh)) {
            continue
        }

        if (!$ElementMap.ContainsKey($nameZh)) {
            throw "Unknown element '$nameZh' in $Context."
        }

        $items.Add([ordered]@{
            element = $ElementMap[$nameZh]
            name_zh = $nameZh
        }) | Out-Null
    }

    return @($items)
}

function Convert-Stats($Source) {
    if ($null -eq $Source) {
        return $null
    }

    Assert-JsonProperties $Source @("hp", "atk", "matk", "def_val", "mdef", "spd") "stats"

    return [ordered]@{
        hp = ConvertTo-NullableInt (Get-JsonProperty $Source "hp")
        atk = ConvertTo-NullableInt (Get-JsonProperty $Source "atk")
        matk = ConvertTo-NullableInt (Get-JsonProperty $Source "matk")
        def_val = ConvertTo-NullableInt (Get-JsonProperty $Source "def_val")
        mdef = ConvertTo-NullableInt (Get-JsonProperty $Source "mdef")
        spd = ConvertTo-NullableInt (Get-JsonProperty $Source "spd")
    }
}

function Convert-Trait($Source) {
    if ($null -eq $Source) {
        return $null
    }

    Assert-JsonProperties $Source @("name", "desc") "trait"

    return [ordered]@{
        name_zh = Normalize-WikirocoText ([string](Get-JsonProperty $Source "name" ""))
        description_zh = Normalize-WikirocoText ([string](Get-JsonProperty $Source "desc" ""))
    }
}

function Convert-Restrain($Source, [string]$Context) {
    if ($null -eq $Source) {
        return $null
    }

    Assert-JsonProperties $Source @("strong_against", "weak_against", "resist", "resisted") "$Context restrain"

    return [ordered]@{
        strong_against = @(Convert-ElementReferenceList (Get-JsonProperty $Source "strong_against" @()) "$Context restrain.strong_against")
        weak_against = @(Convert-ElementReferenceList (Get-JsonProperty $Source "weak_against" @()) "$Context restrain.weak_against")
        resist = @(Convert-ElementReferenceList (Get-JsonProperty $Source "resist" @()) "$Context restrain.resist")
        resisted = @(Convert-ElementReferenceList (Get-JsonProperty $Source "resisted" @()) "$Context restrain.resisted")
    }
}

function Convert-DefensiveTypeChart($Source, [string]$Context) {
    if ($null -eq $Source) {
        return $null
    }

    Assert-JsonProperties $Source @("defender_attrs", "cells") "$Context defensive type chart"

    $cells = [System.Collections.Generic.List[object]]::new()
    foreach ($cell in @((Get-JsonProperty $Source "cells" @()))) {
        Assert-JsonProperties $cell @("attacker_attr", "multiplier", "label", "bucket") "$Context defensive type chart cell"

        $attackerZh = Normalize-WikirocoText ([string](Get-JsonProperty $cell "attacker_attr" ""))
        if (!$ElementMap.ContainsKey($attackerZh)) {
            throw "Unknown attacker attribute '$attackerZh' in $Context defensive type chart."
        }

        $cells.Add([ordered]@{
            attacker_element = $ElementMap[$attackerZh]
            attacker_attr_zh = $attackerZh
            multiplier = [double](Get-JsonProperty $cell "multiplier" 0)
            label = [string](Get-JsonProperty $cell "label" "")
            bucket = [string](Get-JsonProperty $cell "bucket" "")
        }) | Out-Null
    }

    return [ordered]@{
        defender_attributes = @(Convert-ElementReferenceList (Get-JsonProperty $Source "defender_attrs" @()) "$Context defensive type chart defender_attrs")
        cells = @($cells)
    }
}

function Convert-EvolutionChain($Source, [hashtable]$ElfIdByName, [string]$Context) {
    if ($null -eq $Source) {
        return $null
    }

    Assert-JsonProperties $Source @("chain_id", "stages") "$Context evolution chain"

    $stages = [System.Collections.Generic.List[object]]::new()
    foreach ($stage in @((Get-JsonProperty $Source "stages" @()))) {
        Assert-JsonProperties $stage @("sort_order", "next_condition", "pre_condition", "items") "$Context evolution chain stage"

        $stageItems = [System.Collections.Generic.List[object]]::new()
        foreach ($item in @((Get-JsonProperty $stage "items" @()))) {
            Assert-JsonProperties $item @("name", "image_url") "$Context evolution chain stage item"

            $nameZh = Normalize-WikirocoText ([string](Get-JsonProperty $item "name" ""))
            $linkedElfId = if ($ElfIdByName.ContainsKey($nameZh)) { $ElfIdByName[$nameZh] } else { $null }
            $stageItems.Add([ordered]@{
                elf_id = $linkedElfId
                name_zh = $nameZh
                image_url = [string](Get-JsonProperty $item "image_url" "")
            }) | Out-Null
        }

        $stages.Add([ordered]@{
            sort_order = ConvertTo-NullableInt (Get-JsonProperty $stage "sort_order")
            next_condition_zh = Normalize-WikirocoText ([string](Get-JsonProperty $stage "next_condition" ""))
            pre_condition_zh = Normalize-WikirocoText ([string](Get-JsonProperty $stage "pre_condition" ""))
            items = @($stageItems)
        }) | Out-Null
    }

    $sourceChainId = ConvertTo-NullableInt (Get-JsonProperty $Source "chain_id")
    $stageArray = @($stages)

    return [ordered]@{
        id = Get-EvolutionChainId $sourceChainId $stageArray
        source_chain_id = $sourceChainId
        stages = $stageArray
    }
}

function Convert-WikirocoSourceSkill($Source, $SkillIndex, [string]$Context, [int]$SortOrder) {
    Assert-JsonProperties $Source @("name", "attr", "power", "type", "source", "consume", "desc", "icon") "$Context skill"

    $nameZh = Normalize-WikirocoText ([string](Get-JsonProperty $Source "name" ""))
    $attrZh = Normalize-WikirocoText ([string](Get-JsonProperty $Source "attr" ""))
    $typeZh = Normalize-WikirocoText ([string](Get-JsonProperty $Source "type" ""))
    $sourceZh = Normalize-WikirocoText ([string](Get-JsonProperty $Source "source" ""))
    $descriptionZh = Normalize-WikirocoText ([string](Get-JsonProperty $Source "desc" ""))

    if ([string]::IsNullOrWhiteSpace($nameZh)) {
        throw "Missing skill name in $Context."
    }

    if (!$ElementMap.ContainsKey($attrZh)) {
        throw "Unknown skill element '$attrZh' for '$nameZh' in $Context."
    }

    if (!$CategoryMap.ContainsKey($typeZh)) {
        throw "Unknown skill category '$typeZh' for '$nameZh' in $Context."
    }

    $skillId = if ($SkillIndex.ByName.ContainsKey($nameZh)) { [string]$SkillIndex.ByName[$nameZh] } else { $null }
    if ([string]::IsNullOrWhiteSpace($skillId)) {
        throw "Skill '$nameZh' from $Context is not present in Resources/Data/skills.json."
    }

    $skillSource = if ($SkillSourceMap.ContainsKey($sourceZh)) { $SkillSourceMap[$sourceZh] } else { $null }
    if ([string]::IsNullOrWhiteSpace($skillSource)) {
        throw "Unknown skill source '$sourceZh' for '$nameZh' in $Context."
    }

    $canonicalSkill = $SkillIndex.ById[$skillId]
    foreach ($field in @(
            [pscustomobject]@{ Name = "element"; Expected = [string](Get-JsonProperty $canonicalSkill "element" ""); Actual = $ElementMap[$attrZh] },
            [pscustomobject]@{ Name = "category"; Expected = [string](Get-JsonProperty $canonicalSkill "category" ""); Actual = $CategoryMap[$typeZh] },
            [pscustomobject]@{ Name = "energy"; Expected = [string](Get-JsonProperty $canonicalSkill "energy" ""); Actual = [string](ConvertTo-NullableInt (Get-JsonProperty $Source "consume")) },
            [pscustomobject]@{ Name = "power"; Expected = [string](Get-JsonProperty $canonicalSkill "power" ""); Actual = [string](ConvertTo-NullableInt (Get-JsonProperty $Source "power")) },
            [pscustomobject]@{ Name = "description_zh"; Expected = [string](Get-JsonProperty $canonicalSkill "description_zh" ""); Actual = $descriptionZh },
            [pscustomobject]@{ Name = "icon_url"; Expected = [string](Get-JsonProperty (Get-JsonProperty $canonicalSkill "icon") "source_url" ""); Actual = [string](Get-JsonProperty $Source "icon" "") }
        )) {
        if ($field.Expected -ne $field.Actual) {
            throw "Skill '$nameZh' from $Context has $($field.Name) that does not match Resources/Data/skills.json."
        }
    }

    return [ordered]@{
        sort_order = $SortOrder
        skill_id = $skillId
        name_zh = $nameZh
        source = $skillSource
        source_zh = $sourceZh
    }
}

function ConvertTo-StringArray($Values) {
    $items = [System.Collections.Generic.List[string]]::new()
    foreach ($value in @($Values)) {
        $normalized = Normalize-WikirocoText ([string]$value)
        if (![string]::IsNullOrWhiteSpace($normalized)) {
            $items.Add($normalized) | Out-Null
        }
    }

    return @($items)
}

function Get-SourcePayloadHash($Detail, $EvolutionChain) {
    $source = [ordered]@{
        detail = $Detail
        evolution_chain = $EvolutionChain
    }

    return "sha256:" + (Get-Sha256Hex ($source | ConvertTo-Json -Depth 100 -Compress))
}

function Convert-WikirocoSourceElf($Source, $EvolutionChain, $EvolutionChainId, $SkillIndex) {
    Assert-JsonProperties $Source @(
        "id",
        "no",
        "name",
        "image_url",
        "image_yise_url",
        "type",
        "type_name",
        "form",
        "form_name",
        "attributes",
        "egg_groups",
        "obtain_method",
        "stats",
        "trait",
        "restrain",
        "skills",
        "defensive_type_chart"
    ) "elf detail"

    $wikirocoId = [int](Get-JsonProperty $Source "id")
    $nameZh = Normalize-WikirocoText ([string](Get-JsonProperty $Source "name" ""))
    if ([string]::IsNullOrWhiteSpace($nameZh)) {
        throw "Wikiroco source elf id $wikirocoId is missing a name."
    }

    $skills = [System.Collections.Generic.List[object]]::new()
    $skillOrder = 1
    foreach ($skill in @((Get-JsonProperty $Source "skills" @()))) {
        $skills.Add((Convert-WikirocoSourceSkill $skill $SkillIndex $nameZh $skillOrder)) | Out-Null
        $skillOrder++
    }

    $elf = [ordered]@{
        id = Get-ElfId $wikirocoId
        source_key = [ordered]@{
            provider = "wikiroco"
            pokemon_id = $wikirocoId
            name_zh = $nameZh
            no = Normalize-WikirocoText ([string](Get-JsonProperty $Source "no" ""))
        }
        wikiroco_id = $wikirocoId
        no = Normalize-WikirocoText ([string](Get-JsonProperty $Source "no" ""))
        name_zh = $nameZh
        image_url = [string](Get-JsonProperty $Source "image_url" "")
        image_yise_url = [string](Get-JsonProperty $Source "image_yise_url" "")
        type = [string](Get-JsonProperty $Source "type" "")
        type_name_zh = Normalize-WikirocoText ([string](Get-JsonProperty $Source "type_name" ""))
        form = [string](Get-JsonProperty $Source "form" "")
        form_name_zh = Normalize-WikirocoText ([string](Get-JsonProperty $Source "form_name" ""))
        attributes = @(Convert-Attributes (Get-JsonProperty $Source "attributes" @()) $nameZh)
        egg_groups_zh = @(ConvertTo-StringArray (Get-JsonProperty $Source "egg_groups" @()))
        obtain_method_zh = Normalize-WikirocoText ([string](Get-JsonProperty $Source "obtain_method" ""))
        stats = Convert-Stats (Get-JsonProperty $Source "stats")
        trait = Convert-Trait (Get-JsonProperty $Source "trait")
        restrain = Convert-Restrain (Get-JsonProperty $Source "restrain") $nameZh
        skills = @($skills)
        defensive_type_chart = Convert-DefensiveTypeChart (Get-JsonProperty $Source "defensive_type_chart") $nameZh
        evolution_chain_id = $EvolutionChainId
        source_hash = Get-SourcePayloadHash $Source $EvolutionChain
    }

    return $elf
}

function Get-DatasetHash($Elves, $EvolutionChains) {
    $source = [ordered]@{
        elves = @($Elves | ForEach-Object {
            [ordered]@{
                id = [string](Get-JsonProperty $_ "id")
                source_hash = [string](Get-JsonProperty $_ "source_hash")
            }
        })
        evolution_chains = @($EvolutionChains | ForEach-Object {
            [ordered]@{
                id = [string](Get-JsonProperty $_ "id")
                source_chain_id = ConvertTo-NullableInt (Get-JsonProperty $_ "source_chain_id")
                stages = Get-JsonProperty $_ "stages"
            }
        })
    }

    return "sha256:" + (Get-Sha256Hex ($source | ConvertTo-Json -Depth 20 -Compress))
}

function Get-ElfIndex($Elves) {
    $index = @{}
    foreach ($elf in @($Elves)) {
        $id = [string](Get-JsonProperty $elf "id")
        if ([string]::IsNullOrWhiteSpace($id)) {
            throw "Elf record is missing an id."
        }

        if ($index.ContainsKey($id)) {
            throw "Duplicate elf id: $id"
        }

        $index[$id] = $elf
    }

    return $index
}

function Get-EvolutionChainIndex($EvolutionChains) {
    $index = @{}
    foreach ($chain in @($EvolutionChains)) {
        $chainId = [string](Get-JsonProperty $chain "id")
        if ([string]::IsNullOrWhiteSpace($chainId)) {
            throw "Evolution chain record is missing id."
        }

        if ($index.ContainsKey($chainId)) {
            throw "Duplicate evolution chain id: $chainId"
        }

        $index[$chainId] = $chain
    }

    return $index
}


function Get-ElfSkillLinkStats($Elves) {
    $total = 0
    $linked = 0
    foreach ($elf in @($Elves)) {
        foreach ($skill in @((Get-JsonProperty $elf "skills" @()))) {
            $total++
            if (![string]::IsNullOrWhiteSpace([string](Get-JsonProperty $skill "skill_id" ""))) {
                $linked++
            }
        }
    }

    return [pscustomobject]@{
        Total = $total
        Linked = $linked
        Unlinked = $total - $linked
    }
}

function Get-RetryDelaySeconds($ErrorRecord, [int]$Attempt) {
    $retryAfter = $null
    try {
        $retryAfter = $ErrorRecord.Exception.Response.Headers["Retry-After"]
    }
    catch {
        $retryAfter = $null
    }

    $retryAfterSeconds = 0
    if ($retryAfter -and [int]::TryParse([string]$retryAfter, [ref]$retryAfterSeconds)) {
        return [Math]::Min(60, $retryAfterSeconds)
    }

    $jitterMs = Get-Random -Minimum 0 -Maximum 500
    return [Math]::Min(60, ($HttpRetryBaseDelaySec * [Math]::Pow(2, $Attempt - 1)) + ($jitterMs / 1000.0))
}

function Invoke-WikirocoJson([string]$Uri, [string]$Context) {
    for ($attempt = 1; $attempt -le ($HttpRetryCount + 1); $attempt++) {
        try {
            return Invoke-RestMethod -Uri $Uri -TimeoutSec $TimeoutSec
        }
        catch {
            if ($attempt -gt $HttpRetryCount) {
                throw "Wikiroco request failed for ${Context}: $Uri. $($_.Exception.Message)"
            }

            $delaySeconds = Get-RetryDelaySeconds $_ $attempt
            Write-Warning "Wikiroco request failed for ${Context}; retrying in $([Math]::Round($delaySeconds, 2))s ($attempt/$HttpRetryCount)."
            Start-Sleep -Seconds $delaySeconds
        }
    }
}

function Get-WikirocoElfSummaries {
    Write-Host "Fetching elf list from $ElfApiUrl"
    $first = Invoke-WikirocoJson "${ElfApiUrl}?page=1&page_size=$PageSize" "pokemon list page 1"
    $total = [int](Get-JsonProperty $first "total" 0)
    $pageCount = [Math]::Ceiling($total / $PageSize)
    $summaries = [System.Collections.Generic.List[object]]::new()
    foreach ($item in @($first.items)) {
        Assert-JsonProperties $item @("id", "no", "name", "image_url", "image_yise_url", "type", "type_name", "form", "form_name", "attributes", "egg_groups") "pokemon list item"
        $summaries.Add($item) | Out-Null
    }

    if ($pageCount -gt 1) {
        for ($page = 2; $page -le $pageCount; $page++) {
            $result = Invoke-WikirocoJson "${ElfApiUrl}?page=$page&page_size=$PageSize" "pokemon list page $page"
            foreach ($item in @($result.items)) {
                Assert-JsonProperties $item @("id", "no", "name", "image_url", "image_yise_url", "type", "type_name", "form", "form_name", "attributes", "egg_groups") "pokemon list item"
                $summaries.Add($item) | Out-Null
            }
        }
    }

    if ($total -ne $summaries.Count) {
        throw "Wikiroco API total ($total) does not match paged item count ($($summaries.Count))."
    }

    $duplicateNames = @($summaries | Group-Object name | Where-Object Count -gt 1)
    if ($duplicateNames.Count -gt 0) {
        $sample = ($duplicateNames | Select-Object -First 5 | ForEach-Object { $_.Name }) -join ", "
        throw "Wikiroco elf list contains duplicate names: $sample"
    }

    return [pscustomobject]@{
        Total = $total
        Summaries = @($summaries)
    }
}

function Get-WikirocoElfDetails($Summaries) {
    Write-Host "Fetching $(@($Summaries).Count) elf detail/evolution records with throttle $DetailThrottle"
    $results = @($Summaries | ForEach-Object -Parallel {
        function Invoke-WikirocoJsonWithRetry([string]$Uri, [string]$Context) {
            for ($attempt = 1; $attempt -le ($using:HttpRetryCount + 1); $attempt++) {
                try {
                    return Invoke-RestMethod -Uri $Uri -TimeoutSec $using:TimeoutSec
                }
                catch {
                    if ($attempt -gt $using:HttpRetryCount) {
                        throw "Wikiroco request failed for ${Context}: $Uri. $($_.Exception.Message)"
                    }

                    $retryAfterSeconds = 0
                    try {
                        $retryAfter = $_.Exception.Response.Headers["Retry-After"]
                        if ($retryAfter -and [int]::TryParse([string]$retryAfter, [ref]$retryAfterSeconds)) {
                            $delaySeconds = [Math]::Min(60, $retryAfterSeconds)
                        }
                        else {
                            $delaySeconds = [Math]::Min(60, ($using:HttpRetryBaseDelaySec * [Math]::Pow(2, $attempt - 1)) + ((Get-Random -Minimum 0 -Maximum 500) / 1000.0))
                        }
                    }
                    catch {
                        $delaySeconds = [Math]::Min(60, ($using:HttpRetryBaseDelaySec * [Math]::Pow(2, $attempt - 1)) + ((Get-Random -Minimum 0 -Maximum 500) / 1000.0))
                    }

                    Write-Warning "Wikiroco request failed for ${Context}; retrying in $([Math]::Round($delaySeconds, 2))s ($attempt/$using:HttpRetryCount)."
                    Start-Sleep -Seconds $delaySeconds
                }
            }
        }

        $requestedId = [int]$_.id
        $name = [string]$_.name
        $encoded = [Uri]::EscapeDataString($name)
        $detail = Invoke-WikirocoJsonWithRetry "$using:ElfApiUrl/$encoded" "pokemon detail $name"
        $evolution = Invoke-WikirocoJsonWithRetry "$using:ElfApiUrl/evolution-chain/$encoded" "pokemon evolution chain $name"
        [pscustomobject]@{
            RequestedId = $requestedId
            RequestedName = $name
            Detail = $detail
            EvolutionChain = $evolution
        }
    } -ThrottleLimit $DetailThrottle)

    if ($results.Count -ne @($Summaries).Count) {
        throw "Expected $(@($Summaries).Count) detail records, got $($results.Count)."
    }

    foreach ($result in $results) {
        $actualId = [int](Get-JsonProperty $result.Detail "id" 0)
        $actualName = Normalize-WikirocoText ([string](Get-JsonProperty $result.Detail "name" ""))
        if ($actualId -ne $result.RequestedId -or $actualName -ne $result.RequestedName) {
            throw "Wikiroco detail response mismatch for '$($result.RequestedName)': got id=$actualId name='$actualName'."
        }
    }

    return @($results | Sort-Object { [int](Get-JsonProperty $_.Detail "id") })
}

function Get-WikirocoElfSnapshot($SkillIndex) {
    $list = Get-WikirocoElfSummaries
    $elfIdByName = @{}
    foreach ($summary in @($list.Summaries)) {
        $nameZh = Normalize-WikirocoText ([string](Get-JsonProperty $summary "name" ""))
        $elfIdByName[$nameZh] = Get-ElfId ([int](Get-JsonProperty $summary "id"))
    }

    $details = Get-WikirocoElfDetails $list.Summaries
    $elves = [System.Collections.Generic.List[object]]::new()
    $evolutionChainsById = @{}
    foreach ($record in @($details)) {
        $nameZh = Normalize-WikirocoText ([string](Get-JsonProperty $record.Detail "name" ""))
        $evolutionChain = Convert-EvolutionChain $record.EvolutionChain $elfIdByName $nameZh
        $evolutionChainId = [string](Get-JsonProperty $evolutionChain "id")
        $chainJson = $evolutionChain | ConvertTo-Json -Depth 50 -Compress
        if ($evolutionChainsById.ContainsKey($evolutionChainId)) {
            $existingJson = $evolutionChainsById[$evolutionChainId] | ConvertTo-Json -Depth 50 -Compress
            if ($existingJson -ne $chainJson) {
                throw "Wikiroco returned conflicting evolution chain data for $evolutionChainId."
            }
        }
        else {
            $evolutionChainsById[$evolutionChainId] = $evolutionChain
        }

        $elves.Add((Convert-WikirocoSourceElf $record.Detail $record.EvolutionChain $evolutionChainId $SkillIndex)) | Out-Null
    }

    $evolutionChains = @($evolutionChainsById.Values | Sort-Object { [string](Get-JsonProperty $_ "id") })

    return [pscustomobject]@{
        Total = $list.Total
        Elves = @($elves)
        EvolutionChains = @($evolutionChains)
        ById = Get-ElfIndex $elves
        EvolutionChainById = Get-EvolutionChainIndex $evolutionChains
    }
}

function Save-JsonFile([string]$Path, $Value) {
    $json = $Value | ConvertTo-Json -Depth 100
    $json = $json -replace "`r`n", "`n"
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $json + "`n", $utf8NoBom)
}

function Test-ElfDatabase([string]$Path) {
    if (!(Test-Path $Path)) {
        throw "Elf database is missing: $Path"
    }

    $skillIndex = Get-SkillBundleIndex
    $data = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    $elves = @($data.elves)
    $evolutionChains = @($data.evolution_chains)
    $errors = [System.Collections.Generic.List[string]]::new()

    if ([int](Get-JsonProperty $data "schema_version" 0) -ne 1) {
        $errors.Add("schema_version must be 1.")
    }

    $source = Get-JsonProperty $data "source"
    if ([string](Get-JsonProperty $source "provider" "") -ne "wikiroco") {
        $errors.Add("source.provider must be wikiroco.")
    }

    if ([string](Get-JsonProperty $source "url" "") -ne $ElfApiUrl) {
        $errors.Add("source.url must be $ElfApiUrl.")
    }

    if ([string](Get-JsonProperty $source "detail_url_template" "") -ne "$ElfApiUrl/{name_zh}") {
        $errors.Add("source.detail_url_template must be $ElfApiUrl/{name_zh}.")
    }

    if ([string](Get-JsonProperty $source "evolution_chain_url_template" "") -ne "$ElfApiUrl/evolution-chain/{name_zh}") {
        $errors.Add("source.evolution_chain_url_template must be $ElfApiUrl/evolution-chain/{name_zh}.")
    }

    $fetchedAtValue = Get-JsonProperty $source "fetched_at" ""
    $fetchedAt = if ($fetchedAtValue -is [DateTime]) {
        $fetchedAtValue.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", [Globalization.CultureInfo]::InvariantCulture)
    }
    else {
        [string]$fetchedAtValue
    }
    if ([string]::IsNullOrWhiteSpace($fetchedAt) -or $fetchedAt -notmatch "^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$") {
        $errors.Add("source.fetched_at must be a UTC timestamp like yyyy-MM-ddTHH:mm:ssZ.")
    }

    if ($elves.Count -ne [int](Get-JsonProperty $source "item_count" 0)) {
        $errors.Add("source.item_count does not match elves array count.")
    }

    if ([int](Get-JsonProperty $source "source_count" 0) -ne $elves.Count) {
        $errors.Add("source.source_count must match elves array count.")
    }

    $datasetHash = [string](Get-JsonProperty $source "dataset_hash" "")
    if ($datasetHash -notmatch "^sha256:[0-9a-f]{64}$") {
        $errors.Add("source.dataset_hash is invalid.")
    }
    elseif ($datasetHash -ne (Get-DatasetHash $elves $evolutionChains)) {
        $errors.Add("source.dataset_hash does not match elves.")
    }

    foreach ($duplicate in @($elves | Group-Object id | Where-Object Count -gt 1)) {
        $errors.Add("Duplicate elf id: $($duplicate.Name)")
    }

    foreach ($duplicate in @($elves | Group-Object name_zh | Where-Object Count -gt 1)) {
        $errors.Add("Duplicate Chinese elf name: $($duplicate.Name)")
    }

    if ($evolutionChains.Count -ne [int](Get-JsonProperty $source "evolution_chain_count" 0)) {
        $errors.Add("source.evolution_chain_count does not match evolution_chains array count.")
    }

    foreach ($duplicate in @($evolutionChains | Group-Object id | Where-Object Count -gt 1)) {
        $errors.Add("Duplicate evolution chain id: $($duplicate.Name)")
    }

    $evolutionChainById = @{}
    foreach ($chain in $evolutionChains) {
        $chainId = [string](Get-JsonProperty $chain "id" "")
        if ([string]::IsNullOrWhiteSpace($chainId)) {
            $errors.Add("Evolution chain is missing id.")
            continue
        }

        $evolutionChainById[$chainId] = $chain
    }

    $stats = Get-ElfSkillLinkStats $elves
    if ([int](Get-JsonProperty $source "skill_reference_count" 0) -ne $stats.Total) {
        $errors.Add("source.skill_reference_count does not match elf skill references.")
    }
    if ([int](Get-JsonProperty $source "linked_skill_reference_count" 0) -ne $stats.Linked) {
        $errors.Add("source.linked_skill_reference_count does not match linked elf skill references.")
    }
    if ([int](Get-JsonProperty $source "unlinked_skill_reference_count" 0) -ne $stats.Unlinked) {
        $errors.Add("source.unlinked_skill_reference_count does not match unlinked elf skill references.")
    }
    if ($stats.Unlinked -ne 0) {
        $errors.Add("All elf skill references must be linked.")
    }

    if ([int](Get-JsonProperty $source "skill_bundle_item_count" 0) -ne $skillIndex.Count) {
        $errors.Add("source.skill_bundle_item_count does not match Resources/Data/skills.json.")
    }
    if ([string](Get-JsonProperty $source "skill_bundle_dataset_hash" "") -ne $skillIndex.DatasetHash) {
        $errors.Add("source.skill_bundle_dataset_hash does not match Resources/Data/skills.json.")
    }

    foreach ($elf in $elves) {
        $id = [string](Get-JsonProperty $elf "id" "")
        $wikirocoId = ConvertTo-NullableInt (Get-JsonProperty $elf "wikiroco_id")
        $nameZh = [string](Get-JsonProperty $elf "name_zh" "")
        if ($null -eq $wikirocoId -or [string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($nameZh)) {
            $errors.Add("Elf is missing id, wikiroco_id, or name_zh.")
            continue
        }

        $expectedId = Get-ElfId $wikirocoId
        if ($id -ne $expectedId) {
            $errors.Add("Elf id mismatch: $id should be $expectedId for $nameZh.")
        }

        $sourceHash = [string](Get-JsonProperty $elf "source_hash" "")
        if ($sourceHash -notmatch "^sha256:[0-9a-f]{64}$") {
            $errors.Add("Invalid source_hash: $id")
        }

        $sourceKey = Get-JsonProperty $elf "source_key"
        if ([string](Get-JsonProperty $sourceKey "provider" "") -ne "wikiroco" -or
            [int](Get-JsonProperty $sourceKey "pokemon_id" 0) -ne $wikirocoId -or
            [string](Get-JsonProperty $sourceKey "name_zh" "") -ne $nameZh -or
            [string](Get-JsonProperty $sourceKey "no" "") -ne [string](Get-JsonProperty $elf "no" "")) {
            $errors.Add("source_key mismatch: $id")
        }

        foreach ($field in @("no", "image_url", "type", "type_name_zh", "form", "form_name_zh")) {
            if ([string]::IsNullOrWhiteSpace([string](Get-JsonProperty $elf $field ""))) {
                $errors.Add("Elf $id is missing $field.")
            }
        }

        foreach ($requiredObject in @("stats", "trait", "restrain", "defensive_type_chart")) {
            if ($null -eq (Get-JsonProperty $elf $requiredObject)) {
                $errors.Add("Elf $id is missing $requiredObject.")
            }
        }

        foreach ($attribute in @((Get-JsonProperty $elf "attributes" @()))) {
            if (@($ElementMap.Values) -notcontains [string](Get-JsonProperty $attribute "element" "")) {
                $errors.Add("Invalid elf attribute on ${id}: $($attribute.element)")
            }
        }

        $attributeElements = @((Get-JsonProperty $elf "attributes" @()) | ForEach-Object { [string](Get-JsonProperty $_ "element" "") } | Sort-Object)
        $defensiveElements = @((Get-JsonProperty (Get-JsonProperty $elf "defensive_type_chart") "defender_attributes" @()) | ForEach-Object { [string](Get-JsonProperty $_ "element" "") } | Sort-Object)
        if (($attributeElements -join "|") -ne ($defensiveElements -join "|")) {
            $errors.Add("Elf $id defensive defender attributes do not match attributes.")
        }

        $evolutionChainId = [string](Get-JsonProperty $elf "evolution_chain_id" "")
        if ([string]::IsNullOrWhiteSpace($evolutionChainId)) {
            $errors.Add("Elf $id is missing evolution_chain_id.")
        }
        elseif (!$evolutionChainById.ContainsKey($evolutionChainId)) {
            $errors.Add("Elf $id references unknown evolution_chain_id $evolutionChainId.")
        }

        foreach ($skill in @((Get-JsonProperty $elf "skills" @()))) {
            $skillId = [string](Get-JsonProperty $skill "skill_id" "")
            $skillName = [string](Get-JsonProperty $skill "name_zh" "")
            $sortOrder = ConvertTo-NullableInt (Get-JsonProperty $skill "sort_order")
            if ($null -eq $sortOrder -or $sortOrder -lt 1) {
                $errors.Add("Elf $id has invalid skill sort_order for $skillName.")
            }

            if ([string]::IsNullOrWhiteSpace($skillName)) {
                $errors.Add("Elf $id has a skill without name_zh.")
            }

            $skillSource = [string](Get-JsonProperty $skill "source" "")
            $skillSourceZh = [string](Get-JsonProperty $skill "source_zh" "")
            if (@($SkillSourceMap.Values) -notcontains $skillSource) {
                $errors.Add("Elf $id skill $skillName has invalid source: $skillSource.")
            }
            if (!$SkillSourceMap.ContainsKey($skillSourceZh) -or $SkillSourceMap[$skillSourceZh] -ne $skillSource) {
                $errors.Add("Elf $id skill $skillName has invalid source_zh/source pair.")
            }

            if ([string]::IsNullOrWhiteSpace($skillId)) {
                $errors.Add("Elf $id has unlinked skill: $skillName")
            }
            elseif (!$skillIndex.Ids.Contains($skillId)) {
                $errors.Add("Elf $id references unknown skill id $skillId for $skillName.")
            }
            else {
                if (!$skillIndex.ByName.ContainsKey($skillName) -or [string]$skillIndex.ByName[$skillName] -ne $skillId) {
                    $errors.Add("Elf $id skill $skillName does not point to the matching bundled skill id.")
                }

                $canonicalSkill = $skillIndex.ById[$skillId]
                $canonicalName = [string](Get-JsonProperty $canonicalSkill "name_zh" "")
                if ($canonicalName -ne $skillName) {
                    $errors.Add("Elf $id skill $skillName name_zh does not match bundled skill $skillId.")
                }
            }
        }
    }

    $elfIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($elf in $elves) {
        [void]$elfIds.Add([string](Get-JsonProperty $elf "id" ""))
    }

    foreach ($chain in $evolutionChains) {
        $chainId = [string](Get-JsonProperty $chain "id" "")
        foreach ($stage in @((Get-JsonProperty $chain "stages" @()))) {
            foreach ($item in @((Get-JsonProperty $stage "items" @()))) {
                $linkedElfId = [string](Get-JsonProperty $item "elf_id" "")
                if (![string]::IsNullOrWhiteSpace($linkedElfId) -and !$elfIds.Contains($linkedElfId)) {
                    $errors.Add("Evolution chain $chainId references unknown elf id $linkedElfId.")
                }
            }
        }
    }

    if ($errors.Count -gt 0) {
        $errors | ForEach-Object { Write-Error $_ }
        throw "Elf database validation failed with $($errors.Count) error(s)."
    }

    Write-Host "Validation passed: $($elves.Count) elves, $($stats.Linked)/$($stats.Total) skill references linked."
}

function Test-WikirocoFreshness([string]$Path) {
    Test-ElfDatabase $Path

    $existingData = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    $existingElves = @($existingData.elves)
    $existingById = Get-ElfIndex $existingElves
    $existingEvolutionChains = @($existingData.evolution_chains)
    $existingEvolutionChainById = Get-EvolutionChainIndex $existingEvolutionChains
    $skillIndex = Get-SkillBundleIndex
    $snapshot = Get-WikirocoElfSnapshot $skillIndex

    $newElves = [System.Collections.Generic.List[object]]::new()
    $changedElves = [System.Collections.Generic.List[object]]::new()
    $removedElves = [System.Collections.Generic.List[object]]::new()
    $newEvolutionChains = [System.Collections.Generic.List[object]]::new()
    $changedEvolutionChains = [System.Collections.Generic.List[object]]::new()
    $removedEvolutionChains = [System.Collections.Generic.List[object]]::new()

    foreach ($elf in @($snapshot.Elves)) {
        $id = [string]$elf.id
        if (!$existingById.ContainsKey($id)) {
            $newElves.Add($elf) | Out-Null
            continue
        }

        $existing = $existingById[$id]
        if ([string](Get-JsonProperty $existing "source_hash" "") -ne [string]$elf.source_hash) {
            $changedElves.Add($elf) | Out-Null
        }
    }

    foreach ($existing in $existingElves) {
        if (!$snapshot.ById.ContainsKey([string](Get-JsonProperty $existing "id"))) {
            $removedElves.Add($existing) | Out-Null
        }
    }

    foreach ($chain in @($snapshot.EvolutionChains)) {
        $chainId = [string](Get-JsonProperty $chain "id")
        if (!$existingEvolutionChainById.ContainsKey($chainId)) {
            $newEvolutionChains.Add($chain) | Out-Null
            continue
        }

        $existingJson = $existingEvolutionChainById[$chainId] | ConvertTo-Json -Depth 50 -Compress
        $snapshotJson = $chain | ConvertTo-Json -Depth 50 -Compress
        if ($existingJson -ne $snapshotJson) {
            $changedEvolutionChains.Add($chain) | Out-Null
        }
    }

    foreach ($existing in $existingEvolutionChains) {
        if (!$snapshot.EvolutionChainById.ContainsKey([string](Get-JsonProperty $existing "id"))) {
            $removedEvolutionChains.Add($existing) | Out-Null
        }
    }

    $existingSource = Get-JsonProperty $existingData "source"
    $snapshotDatasetHash = Get-DatasetHash $snapshot.Elves $snapshot.EvolutionChains
    $countChanged = [int](Get-JsonProperty $existingSource "source_count" 0) -ne $snapshot.Total -or
        [int](Get-JsonProperty $existingSource "item_count" 0) -ne $snapshot.Elves.Count -or
        $existingElves.Count -ne $snapshot.Elves.Count -or
        [int](Get-JsonProperty $existingSource "evolution_chain_count" 0) -ne $snapshot.EvolutionChains.Count -or
        $existingEvolutionChains.Count -ne $snapshot.EvolutionChains.Count
    $skillBundleChanged = [string](Get-JsonProperty $existingSource "skill_bundle_dataset_hash" "") -ne $skillIndex.DatasetHash
    $datasetHashChanged = [string](Get-JsonProperty $existingSource "dataset_hash" "") -ne $snapshotDatasetHash

    Write-Host "Local count: $($existingElves.Count)"
    Write-Host "Remote total: $($snapshot.Total)"
    Write-Host "Remote items: $($snapshot.Elves.Count)"
    Write-Host "New: $($newElves.Count)"
    Write-Host "Changed: $($changedElves.Count)"
    Write-Host "Removed: $($removedElves.Count)"
    Write-Host "Evolution chains new: $($newEvolutionChains.Count)"
    Write-Host "Evolution chains changed: $($changedEvolutionChains.Count)"
    Write-Host "Evolution chains removed: $($removedEvolutionChains.Count)"
    Write-Host "Skill bundle changed: $skillBundleChanged"
    Write-Host "Dataset hash changed: $datasetHashChanged"

    $needsUpdate = $countChanged -or
        $skillBundleChanged -or
        $datasetHashChanged -or
        $newElves.Count -gt 0 -or
        $changedElves.Count -gt 0 -or
        $removedElves.Count -gt 0 -or
        $newEvolutionChains.Count -gt 0 -or
        $changedEvolutionChains.Count -gt 0 -or
        $removedEvolutionChains.Count -gt 0
    if ($needsUpdate) {
        Write-Warning "Bundled wikiroco elf data is not current. Run tools\update-elves.ps1 to update it."
        return $false
    }

    Write-Host "Upstream freshness check passed: bundled wikiroco elf data is current."
    return $true
}

function Save-ElfDataWithBackup($Output) {
    $backupDir = Join-Path $RepoRoot "data\elf-backups"
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

    if (Test-Path $DataPath) {
        $backupPath = Join-Path $backupDir ("elves.json.bak-{0}" -f (Get-Date -Format yyyyMMddHHmmss))
        Copy-Item -LiteralPath $DataPath -Destination $backupPath
        Write-Host "Backup written: $backupPath"
    }

    $tempDataPath = Join-Path ([System.IO.Path]::GetTempPath()) ("peek-elves-" + [Guid]::NewGuid().ToString("N") + ".json")
    try {
        Save-JsonFile $tempDataPath $Output
        Test-ElfDatabase $tempDataPath
        Move-Item -LiteralPath $tempDataPath -Destination $DataPath -Force
    }
    finally {
        if (Test-Path $tempDataPath) {
            Remove-Item -LiteralPath $tempDataPath -Force
        }
    }

    Write-Host "Elf database updated: $DataPath"
}

function Test-ExistingElfDataMatches($Output) {
    if (!(Test-Path $DataPath)) {
        return $false
    }

    try {
        $existingData = Get-Content -LiteralPath $DataPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $existingSource = Get-JsonProperty $existingData "source"
        $newSource = Get-JsonProperty $Output "source"
        return [string](Get-JsonProperty $existingSource "dataset_hash" "") -eq [string](Get-JsonProperty $newSource "dataset_hash" "") -and
            [string](Get-JsonProperty $existingSource "skill_bundle_dataset_hash" "") -eq [string](Get-JsonProperty $newSource "skill_bundle_dataset_hash" "") -and
            (ConvertTo-UtcTimestampString (Get-JsonProperty $existingSource "skill_bundle_fetched_at" "")) -eq (ConvertTo-UtcTimestampString (Get-JsonProperty $newSource "skill_bundle_fetched_at" ""))
    }
    catch {
        return $false
    }
}

if ($ValidateOnly -and $CheckFreshness) {
    throw "Use either -ValidateOnly or -CheckFreshness, not both."
}

if ($ValidateOnly) {
    Test-ElfDatabase $DataPath
    return
}

if ($CheckFreshness) {
    if (Test-WikirocoFreshness $DataPath) {
        return
    }

    exit 1
}

$skillIndex = Get-SkillBundleIndex
$snapshot = Get-WikirocoElfSnapshot $skillIndex
$linkStats = Get-ElfSkillLinkStats $snapshot.Elves
$fetchedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", [Globalization.CultureInfo]::InvariantCulture)

$output = [ordered]@{
    schema_version = 1
    source = [ordered]@{
        provider = "wikiroco"
        url = $ElfApiUrl
        detail_url_template = "$ElfApiUrl/{name_zh}"
        evolution_chain_url_template = "$ElfApiUrl/evolution-chain/{name_zh}"
        fetched_at = $fetchedAt
        source_count = $snapshot.Total
        item_count = $snapshot.Elves.Count
        skill_bundle_url = "https://wikiroco.com/api/skills"
        skill_bundle_item_count = $skillIndex.Count
        skill_bundle_dataset_hash = $skillIndex.DatasetHash
        skill_bundle_fetched_at = $skillIndex.FetchedAt
        skill_reference_count = $linkStats.Total
        linked_skill_reference_count = $linkStats.Linked
        unlinked_skill_reference_count = $linkStats.Unlinked
        evolution_chain_count = $snapshot.EvolutionChains.Count
        dataset_hash = Get-DatasetHash $snapshot.Elves $snapshot.EvolutionChains
    }
    elves = @($snapshot.Elves)
    evolution_chains = @($snapshot.EvolutionChains)
}

if (Test-ExistingElfDataMatches $output) {
    Write-Host "Elf database already current; no changes written."
    return
}

Save-ElfDataWithBackup $output
