param(
    [switch]$TranslateChanged,
    [switch]$RefreshIcons,
    [switch]$ValidateOnly,
    [switch]$AllowMissingTranslations,
    [string]$GeminiApiKey = $env:GEMINI_API_KEY,
    [string]$GeminiModel = "gemini-3.1-flash-lite",
    [int]$TranslationBatchSize = 60,
    [int]$IconDownloadTimeoutSec = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptRoot
$DataPath = Join-Path $RepoRoot "Resources\Data\skills.json"
$SkillIconDir = Join-Path $RepoRoot "Resources\Skills"
$ElementIconDir = Join-Path $RepoRoot "Resources\Elements"
$SkillMetaIconDir = Join-Path $RepoRoot "Resources\SkillMeta"
$SkillApiUrl = "https://wikiroco.com/api/skills"
$RocomwikiIconBaseUrl = "https://rocomwiki.app/icons"
$HttpClient = [System.Net.Http.HttpClient]::new()
$HttpClient.Timeout = [TimeSpan]::FromSeconds($IconDownloadTimeoutSec)

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

$SkillMetaIconMap = @{
    "Physical.xaml" = @{
        Url = "$RocomwikiIconBaseUrl/stats/atk.svg"
        Brush = "#FFFF715F"
    }
    "Magic.xaml" = @{
        Url = "$RocomwikiIconBaseUrl/stats/spa.svg"
        Brush = "#FF9C8CFF"
    }
    "Defense.xaml" = @{
        Url = "$RocomwikiIconBaseUrl/stats/def.svg"
        Brush = "#FF65D18D"
    }
    "Status.xaml" = @{
        Url = "$RocomwikiIconBaseUrl/skill-categories/status.svg"
        Brush = "#FFFFD76B"
    }
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

function Get-SkillId([string]$NameZh) {
    return "wikiroco-" + (Get-Sha256Hex $NameZh).Substring(0, 12)
}

function Get-SourceHash($Skill) {
    $source = [ordered]@{
        name_zh = [string](Get-JsonProperty $Skill "name_zh")
        element = [string](Get-JsonProperty $Skill "element")
        category = [string](Get-JsonProperty $Skill "category")
        energy = Get-JsonProperty $Skill "energy"
        power = Get-JsonProperty $Skill "power"
        description_zh = [string](Get-JsonProperty $Skill "description_zh")
    }

    return Get-Sha256Hex ($source | ConvertTo-Json -Depth 8 -Compress)
}

function Convert-Localization($Localization) {
    if ($null -eq $Localization) {
        return $null
    }

    $name = [string](Get-JsonProperty $Localization "name" "")
    $description = [string](Get-JsonProperty $Localization "description" "")
    if ([string]::IsNullOrWhiteSpace($name) -or $null -eq (Get-JsonProperty $Localization "description")) {
        return $null
    }

    return [ordered]@{
        name = $name
        description = $description
    }
}

function Convert-LocalizationSet($Localized) {
    return [ordered]@{
        en = Convert-Localization (Get-JsonProperty $Localized "en")
        vi = Convert-Localization (Get-JsonProperty $Localized "vi")
    }
}

function Test-LocalizationSet($Localized) {
    $en = Get-JsonProperty $Localized "en"
    $vi = Get-JsonProperty $Localized "vi"
    return -not [string]::IsNullOrWhiteSpace([string](Get-JsonProperty $en "name" "")) -and
        $null -ne (Get-JsonProperty $en "description") -and
        -not [string]::IsNullOrWhiteSpace([string](Get-JsonProperty $vi "name" "")) -and
        $null -ne (Get-JsonProperty $vi "description")
}

function Save-ImageAsPng([string]$InputPath, [string]$OutputPath, [int]$Width, [int]$Height) {
    Add-Type -AssemblyName PresentationCore
    Add-Type -AssemblyName WindowsBase

    $stream = [System.IO.File]::OpenRead($InputPath)
    try {
        $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
            $stream,
            [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        $source = $decoder.Frames[0]
    }
    finally {
        $stream.Dispose()
    }

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $context.DrawImage($source, [System.Windows.Rect]::new(0, 0, $Width, $Height))
    }
    finally {
        $context.Close()
    }

    $target = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Width,
        $Height,
        96,
        96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $target.Render($visual)

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($target))

    $directory = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $output = [System.IO.File]::Create($OutputPath)
    try {
        $encoder.Save($output)
    }
    finally {
        $output.Dispose()
    }
}

function Save-RemoteImageAsPng([string]$Url, [string]$OutputPath, [int]$Width, [int]$Height) {
    $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) ("peek-image-" + [Guid]::NewGuid().ToString("N"))
    try {
        $bytes = $HttpClient.GetByteArrayAsync($Url).GetAwaiter().GetResult()
        [System.IO.File]::WriteAllBytes($tempPath, $bytes)

        $size = Get-ImageSize $tempPath
        $directory = Split-Path -Parent $OutputPath
        New-Item -ItemType Directory -Force -Path $directory | Out-Null

        if ($size.Width -eq $Width -and $size.Height -eq $Height) {
            Copy-Item -LiteralPath $tempPath -Destination $OutputPath -Force
        }
        else {
            Save-ImageAsPng $tempPath $OutputPath $Width $Height
        }
    }
    finally {
        if (Test-Path $tempPath) {
            Remove-Item -LiteralPath $tempPath -Force
        }
    }
}

function Get-ImageSize([string]$Path) {
    Add-Type -AssemblyName PresentationCore

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
            $stream,
            [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        return [pscustomobject]@{
            Width = $decoder.Frames[0].PixelWidth
            Height = $decoder.Frames[0].PixelHeight
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Escape-Xaml([string]$Value) {
    return [System.Security.SecurityElement]::Escape($Value)
}

function Convert-SvgToDrawingImageXaml([string]$SvgText, [string]$FallbackBrush) {
    [xml]$xml = $SvgText
    $svg = $xml.DocumentElement
    $viewBox = [string]$svg.GetAttribute("viewBox")
    if ([string]::IsNullOrWhiteSpace($viewBox)) {
        throw "SVG is missing a viewBox."
    }

    $parts = @($viewBox -split "[,\s]+" | Where-Object { $_ -ne "" })
    if ($parts.Count -ne 4) {
        throw "Unsupported SVG viewBox: $viewBox"
    }

    $culture = [Globalization.CultureInfo]::InvariantCulture
    $x = [double]::Parse($parts[0], $culture)
    $y = [double]::Parse($parts[1], $culture)
    $width = [double]::Parse($parts[2], $culture)
    $height = [double]::Parse($parts[3], $culture)
    $viewportGeometry = "M {0} {1} L {2} {1} L {2} {3} L {0} {3} Z" -f
        $x.ToString($culture),
        $y.ToString($culture),
        ($x + $width).ToString($culture),
        ($y + $height).ToString($culture)

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('<DrawingImage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">')
    $lines.Add('  <DrawingImage.Drawing>')
    $lines.Add('    <DrawingGroup>')
    $lines.Add(('      <GeometryDrawing Brush="Transparent" Geometry="{0}" />' -f $viewportGeometry))

    foreach ($node in $svg.ChildNodes) {
        if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element) {
            continue
        }

        $fill = [string]$node.GetAttribute("fill")
        if ([string]::IsNullOrWhiteSpace($fill) -or $fill -eq "currentColor") {
            $fill = $FallbackBrush
        }

        $fill = Escape-Xaml $fill
        if ($node.LocalName -eq "circle") {
            $cx = Escape-Xaml ([string]$node.GetAttribute("cx"))
            $cy = Escape-Xaml ([string]$node.GetAttribute("cy"))
            $radius = Escape-Xaml ([string]$node.GetAttribute("r"))
            $lines.Add(('      <GeometryDrawing Brush="{0}">' -f $fill))
            $lines.Add('        <GeometryDrawing.Geometry>')
            $lines.Add(('          <EllipseGeometry Center="{0},{1}" RadiusX="{2}" RadiusY="{2}" />' -f $cx, $cy, $radius))
            $lines.Add('        </GeometryDrawing.Geometry>')
            $lines.Add('      </GeometryDrawing>')
            continue
        }

        if ($node.LocalName -ne "path") {
            throw "Unsupported SVG node: $($node.LocalName)"
        }

        $geometry = Escape-Xaml ([string]$node.GetAttribute("d"))
        $transform = [string]$node.GetAttribute("transform")
        $translate = [regex]::Match($transform, "^translate\(([-0-9.]+)\s+([-0-9.]+)\)$")
        if ($translate.Success) {
            $lines.Add('      <DrawingGroup>')
            $lines.Add('        <DrawingGroup.Transform>')
            $lines.Add(('          <TranslateTransform X="{0}" Y="{1}" />' -f $translate.Groups[1].Value, $translate.Groups[2].Value))
            $lines.Add('        </DrawingGroup.Transform>')
            $lines.Add(('        <GeometryDrawing Brush="{0}" Geometry="{1}" />' -f $fill, $geometry))
            $lines.Add('      </DrawingGroup>')
        }
        else {
            $lines.Add(('      <GeometryDrawing Brush="{0}" Geometry="{1}" />' -f $fill, $geometry))
        }
    }

    $lines.Add('    </DrawingGroup>')
    $lines.Add('  </DrawingImage.Drawing>')
    $lines.Add('</DrawingImage>')
    return ($lines -join "`r`n") + "`r`n"
}

function Save-RemoteSvgAsXaml([string]$Url, [string]$OutputPath, [string]$FallbackBrush) {
    $svg = $HttpClient.GetStringAsync($Url).GetAwaiter().GetResult()
    $xaml = Convert-SvgToDrawingImageXaml $svg $FallbackBrush
    $directory = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    Set-Content -LiteralPath $OutputPath -Value $xaml -Encoding UTF8
}

function Update-RocomwikiIconResources([bool]$Force) {
    foreach ($element in @($ElementMap.Values | Sort-Object -Unique)) {
        $path = Join-Path $ElementIconDir "$element.xaml"
        if ($Force -or !(Test-Path $path)) {
            Save-RemoteSvgAsXaml "$RocomwikiIconBaseUrl/elements/$element.svg" $path "#FFFFFFFF"
        }
    }

    foreach ($entry in $SkillMetaIconMap.GetEnumerator()) {
        $path = Join-Path $SkillMetaIconDir $entry.Key
        if ($Force -or !(Test-Path $path)) {
            Save-RemoteSvgAsXaml ([string]$entry.Value.Url) $path ([string]$entry.Value.Brush)
        }
    }
}

function Get-SavedGeminiApiKey {
    Add-Type -AssemblyName System.Security

    $paths = @(
        (Join-Path $RepoRoot "data\settings.json")
    )

    foreach ($path in $paths) {
        if (!(Test-Path $path)) {
            continue
        }

        $settings = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        $encryptedApiKey = [string](Get-JsonProperty $settings "EncryptedApiKey" "")
        if ([string]::IsNullOrWhiteSpace($encryptedApiKey)) {
            continue
        }

        $encryptedBytes = [Convert]::FromBase64String($encryptedApiKey)
        $plainBytes = [Security.Cryptography.ProtectedData]::Unprotect(
            $encryptedBytes,
            $null,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $apiKey = [Text.Encoding]::UTF8.GetString($plainBytes)
        if ($apiKey.StartsWith("AIza", [StringComparison]::OrdinalIgnoreCase)) {
            return $apiKey
        }
    }

    return $null
}

function Invoke-SkillTranslationBatch($Skills, [string]$ApiKey, [string]$Model) {
    $items = @($Skills | ForEach-Object {
        [ordered]@{
            id = $_.id
            name_zh = $_.name_zh
            description_zh = $_.description_zh
            element = $_.element
            category = $_.category
        }
    })

    $schema = [ordered]@{
        type = "object"
        required = @("skills")
        properties = [ordered]@{
            skills = [ordered]@{
                type = "array"
                items = [ordered]@{
                    type = "object"
                    required = @("id", "en", "vi")
                    properties = [ordered]@{
                        id = [ordered]@{ type = "string" }
                        en = [ordered]@{
                            type = "object"
                            required = @("name", "description")
                            properties = [ordered]@{
                                name = [ordered]@{ type = "string" }
                                description = [ordered]@{ type = "string" }
                            }
                        }
                        vi = [ordered]@{
                            type = "object"
                            required = @("name", "description")
                            properties = [ordered]@{
                                name = [ordered]@{ type = "string" }
                                description = [ordered]@{ type = "string" }
                            }
                        }
                    }
                }
            }
        }
    }

    $systemPrompt = @"
Translate Roco Kingdom: World skill names and descriptions from Simplified Chinese to English and Vietnamese.
Rules:
- Return only JSON matching the schema.
- Keep each id exactly unchanged.
- Translate every name and description; if the Chinese description is empty, return an empty description.
- Preserve numbers, percentages, plus/minus signs, cooldown values, combo counts, energy values, and punctuation meaning.
- Use concise game UI wording, not explanatory notes.
- Use consistent mechanics glossary:
  精灵 = Spirit; 物攻 = Physical Attack; 魔攻 = Magic Attack; 物防 = Physical Defense; 魔防 = Magic Defense; 速度 = Speed; 双攻 = both attacks.
  物伤 = physical damage; 魔伤 = magic damage; 减伤 = damage reduction; 回复 = recover; 驱散 = dispel; 脱离/返场 = switch out; 打断 = interrupt.
  应对攻击 = counter attack; 应对状态 = counter status; 应对防御 = counter defense; 先手+1 = Priority +1; 先手-1 = Priority -1.
  连击 = hits; 中毒 = poison; 灼烧 = burn; 冻结 = freeze; 星陨 = Starfall; 印记 = mark; 威力 = power; 能耗 = energy cost; 冷却 = cooldown; 蓄力 = charge; 迸发 = burst; 迅捷 = swift; 巧变 = morph; 传动 = transmission; 奉献 = devotion.
- Vietnamese should be natural Vietnamese game text. Prefer "Gây sát thương vật lý" and "Gây sát thương phép". Keep common stat labels in Vietnamese: Tấn công vật lý, Tấn công phép, Phòng thủ vật lý, Phòng thủ phép, Tốc độ.
"@

    $payload = [ordered]@{
        systemInstruction = [ordered]@{
            parts = @([ordered]@{ text = $systemPrompt })
        }
        contents = @([ordered]@{
            role = "user"
            parts = @([ordered]@{
                text = "Translate these skills: " + ($items | ConvertTo-Json -Depth 10 -Compress)
            })
        })
        generationConfig = [ordered]@{
            maxOutputTokens = 8192
            responseFormat = [ordered]@{
                text = [ordered]@{
                    mimeType = "APPLICATION_JSON"
                    schema = $schema
                }
            }
            thinkingConfig = [ordered]@{
                thinkingLevel = "minimal"
            }
        }
    }

    $uri = "https://generativelanguage.googleapis.com/v1beta/models/$([Uri]::EscapeDataString($Model)):generateContent"
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $response = Invoke-RestMethod `
                -Method Post `
                -Uri $uri `
                -Headers @{ "x-goog-api-key" = $ApiKey } `
                -ContentType "application/json" `
                -Body ($payload | ConvertTo-Json -Depth 40 -Compress) `
                -TimeoutSec 120
            $text = $response.candidates[0].content.parts[0].text
            if ([string]::IsNullOrWhiteSpace($text)) {
                throw "Empty Gemini response."
            }

            $rows = @(($text | ConvertFrom-Json).skills)
            if ($rows.Count -ne $items.Count) {
                throw "Expected $($items.Count) translated skills, got $($rows.Count)."
            }

            return $rows
        }
        catch {
            if ($attempt -eq 3) {
                throw
            }

            Start-Sleep -Seconds (2 * $attempt)
        }
    }
}

function Test-SkillDatabase([string]$Path, [bool]$RequireTranslations) {
    $data = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    $skills = @($data.skills)
    $errors = [System.Collections.Generic.List[string]]::new()

    if ($skills.Count -ne [int](Get-JsonProperty $data "source_count" 0)) {
        $errors.Add("source_count does not match skills array count.")
    }

    $duplicateIds = @($skills | Group-Object id | Where-Object Count -gt 1)
    foreach ($duplicate in $duplicateIds) {
        $errors.Add("Duplicate skill id: $($duplicate.Name)")
    }

    $duplicateNames = @($skills | Group-Object name_zh | Where-Object Count -gt 1)
    foreach ($duplicate in $duplicateNames) {
        $errors.Add("Duplicate Chinese skill name: $($duplicate.Name)")
    }

    foreach ($skill in $skills) {
        $icon = Join-Path $RepoRoot ([string]$skill.icon)
        if (!(Test-Path $icon)) {
            $errors.Add("Missing skill icon: $($skill.id) -> $($skill.icon)")
            continue
        }

        $size = Get-ImageSize $icon
        if ($size.Width -ne 128 -or $size.Height -ne 128) {
            $errors.Add("Skill icon must be 128x128: $($skill.icon) is $($size.Width)x$($size.Height)")
        }

        if ($RequireTranslations -and !(Test-LocalizationSet $skill.localized)) {
            $errors.Add("Missing EN/VI localization: $($skill.id) $($skill.name_zh)")
        }
    }

    foreach ($element in @($ElementMap.Values | Sort-Object -Unique)) {
        $elementVectorIcon = Join-Path $ElementIconDir "$element.xaml"
        if (!(Test-Path $elementVectorIcon)) {
            $errors.Add("Missing rocomwiki element vector icon: Resources/Elements/$element.xaml")
        }
    }

    foreach ($entry in $SkillMetaIconMap.GetEnumerator()) {
        $metaIcon = Join-Path $SkillMetaIconDir $entry.Key
        if (!(Test-Path $metaIcon)) {
            $errors.Add("Missing rocomwiki skill metadata icon: Resources/SkillMeta/$($entry.Key)")
        }
    }

    if ($errors.Count -gt 0) {
        $errors | ForEach-Object { Write-Error $_ }
        throw "Skill database validation failed with $($errors.Count) error(s)."
    }

    Write-Host "Validation passed: $($skills.Count) skills, all icons present, all skill icons 128x128."
}

if ($ValidateOnly) {
    Test-SkillDatabase $DataPath (-not $AllowMissingTranslations)
    return
}

New-Item -ItemType Directory -Force -Path $SkillIconDir, $ElementIconDir, $SkillMetaIconDir | Out-Null
Update-RocomwikiIconResources $RefreshIcons

$existingData = Get-Content -LiteralPath $DataPath -Raw -Encoding UTF8 | ConvertFrom-Json
$existingById = @{}
foreach ($skill in @($existingData.skills)) {
    $existingById[[string]$skill.id] = $skill
}

Write-Host "Fetching skills from $SkillApiUrl"
$skillResponse = Invoke-RestMethod -Uri $SkillApiUrl
$sourceSkills = @($skillResponse.items)

$newSkills = [System.Collections.Generic.List[object]]::new()
$translationQueue = [System.Collections.Generic.List[object]]::new()
$seenIds = [System.Collections.Generic.HashSet[string]]::new()

foreach ($source in $sourceSkills) {
    $nameZh = [string]$source.name
    $attrZh = [string]$source.attr
    $typeZh = [string]$source.type

    if (!$ElementMap.ContainsKey($attrZh)) {
        throw "Unknown skill element '$attrZh' for '$nameZh'."
    }

    if (!$CategoryMap.ContainsKey($typeZh)) {
        throw "Unknown skill category '$typeZh' for '$nameZh'."
    }

    $id = Get-SkillId $nameZh
    if (!$seenIds.Add($id)) {
        throw "Duplicate generated skill id '$id' for '$nameZh'."
    }

    $element = $ElementMap[$attrZh]
    $category = $CategoryMap[$typeZh]
    $iconRelativePath = "Resources/Skills/$id.png"
    $iconPath = Join-Path $RepoRoot $iconRelativePath
    $iconUrl = [string]$source.icon

    $existing = $null
    if ($existingById.ContainsKey($id)) {
        $existing = $existingById[$id]
    }

    $oldIconUrl = [string](Get-JsonProperty $existing "source_icon_url" "")
    $shouldDownloadIcon = $RefreshIcons -or
        !(Test-Path $iconPath) -or
        (![string]::IsNullOrWhiteSpace($oldIconUrl) -and $oldIconUrl -ne $iconUrl)
    if ($shouldDownloadIcon) {
        Save-RemoteImageAsPng $iconUrl $iconPath 128 128
    }
    elseif (Test-Path $iconPath) {
        $size = Get-ImageSize $iconPath
        if ($size.Width -ne 128 -or $size.Height -ne 128) {
            Save-ImageAsPng $iconPath $iconPath 128 128
        }
    }

    $skill = [ordered]@{
        id = $id
        name_zh = $nameZh
        aliases_zh = @()
        element = $element
        category = $category
        energy = $source.consume
        power = $source.power
        description_zh = [string]$source.desc
        icon = $iconRelativePath
        source_hash = $null
        source_icon_url = $iconUrl
        localized = [ordered]@{
            en = $null
            vi = $null
        }
    }

    $skill.source_hash = Get-SourceHash $skill
    $oldHash = [string](Get-JsonProperty $existing "source_hash" "")
    if ([string]::IsNullOrWhiteSpace($oldHash) -and $null -ne $existing) {
        $oldHash = Get-SourceHash $existing
    }

    if ($null -ne $existing -and $oldHash -eq $skill.source_hash -and (Test-LocalizationSet $existing.localized)) {
        $skill.localized = Convert-LocalizationSet $existing.localized
    }
    else {
        $translationQueue.Add($skill)
        if ($null -ne $existing -and (Test-LocalizationSet $existing.localized)) {
            $skill.localized = Convert-LocalizationSet $existing.localized
        }
    }

    $newSkills.Add($skill)
}

if ($translationQueue.Count -gt 0 -and !$TranslateChanged -and !$AllowMissingTranslations) {
    throw "$($translationQueue.Count) skill(s) are new or changed and need translations. Re-run with -TranslateChanged, or pass -AllowMissingTranslations for a non-release data file."
}

if ($TranslateChanged -and $translationQueue.Count -gt 0) {
    if ([string]::IsNullOrWhiteSpace($GeminiApiKey)) {
        $GeminiApiKey = Get-SavedGeminiApiKey
    }

    if ([string]::IsNullOrWhiteSpace($GeminiApiKey)) {
        throw "Gemini API key is required. Set GEMINI_API_KEY, pass -GeminiApiKey, or save a key in app settings first."
    }

    $totalBatches = [Math]::Ceiling($translationQueue.Count / $TranslationBatchSize)
    $translations = @{}
    for ($i = 0; $i -lt $translationQueue.Count; $i += $TranslationBatchSize) {
        $end = [Math]::Min($i + $TranslationBatchSize - 1, $translationQueue.Count - 1)
        $batch = @($translationQueue[$i..$end])
        $batchNumber = [Math]::Floor($i / $TranslationBatchSize) + 1
        Write-Host "Translating batch $batchNumber/$totalBatches ($($batch.Count) skill(s))"
        $rows = Invoke-SkillTranslationBatch $batch $GeminiApiKey $GeminiModel
        foreach ($row in $rows) {
            $translations[[string]$row.id] = [ordered]@{
                en = [ordered]@{
                    name = [string]$row.en.name
                    description = [string]$row.en.description
                }
                vi = [ordered]@{
                    name = [string]$row.vi.name
                    description = [string]$row.vi.description
                }
            }
        }
    }

    foreach ($skill in $newSkills) {
        if ($translations.ContainsKey([string]$skill.id)) {
            $skill.localized = $translations[[string]$skill.id]
        }
    }
}

$output = [ordered]@{
    schema_version = 1
    source = $SkillApiUrl
    source_count = [int]$skillResponse.total
    last_updated = (Get-Date -Format "yyyy-MM-dd")
    skills = @($newSkills)
}

$backupDir = Join-Path $RepoRoot "data\skill-backups"
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
$backupPath = Join-Path $backupDir ("skills.json.bak-{0}" -f (Get-Date -Format yyyyMMddHHmmss))
Copy-Item -LiteralPath $DataPath -Destination $backupPath
Write-Host "Backup written: $backupPath"

$output | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $DataPath -Encoding UTF8
Test-SkillDatabase $DataPath (-not $AllowMissingTranslations)
Write-Host "Skill database updated: $DataPath"
