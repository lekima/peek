param(
    [switch]$TranslateChanged,
    [switch]$RefreshIcons,
    [switch]$ValidateOnly,
    [switch]$CheckFreshness,
    [switch]$DeepIcons,
    [switch]$AllowMissingTranslations,
    [switch]$BuildTranslationMemory,
    [switch]$HarmonizeTranslations,
    [switch]$SkipHarmonization,
    [string]$GeminiApiKey = $env:GEMINI_API_KEY,
    [string]$GeminiModel = "gemini-3.1-flash-lite",
    [string]$TranslationMemoryPath = "",
    [ValidateRange(1, 500)]
    [int]$TranslationBatchSize = 60,
    [ValidateRange(1, 500)]
    [int]$HarmonizationBatchSize = 60,
    [ValidateRange(10, 500)]
    [int]$TranslationMemoryBatchSize = 80,
    [ValidateRange(1, 300)]
    [int]$IconDownloadTimeoutSec = 30,
    [ValidateRange(1, 64)]
    [int]$IconCheckThrottle = 16
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptRoot
$DataPath = Join-Path $RepoRoot "Resources\Data\skills.json"
$DefaultTranslationMemoryPath = Join-Path $RepoRoot "Resources\Data\translation-memory.json"
if ([string]::IsNullOrWhiteSpace($TranslationMemoryPath)) {
    $TranslationMemoryPath = $DefaultTranslationMemoryPath
}

$SkillIconDir = Join-Path $RepoRoot "Resources\Skills"
$ElementIconDir = Join-Path $RepoRoot "Resources\Elements"
$SkillMetaIconDir = Join-Path $RepoRoot "Resources\SkillMeta"
$SkillApiUrl = "https://wikiroco.com/api/skills"
$RocomwikiIconBaseUrl = "https://rocomwiki.app/icons"
$HttpClient = [System.Net.Http.HttpClient]::new()
$HttpClient.Timeout = [TimeSpan]::FromSeconds($IconDownloadTimeoutSec)
$DisabledSafetySettings = @(
    [ordered]@{
        category = "HARM_CATEGORY_HARASSMENT"
        threshold = "OFF"
    }
    [ordered]@{
        category = "HARM_CATEGORY_HATE_SPEECH"
        threshold = "OFF"
    }
    [ordered]@{
        category = "HARM_CATEGORY_SEXUALLY_EXPLICIT"
        threshold = "OFF"
    }
    [ordered]@{
        category = "HARM_CATEGORY_DANGEROUS_CONTENT"
        threshold = "OFF"
    }
)

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

function Get-BytesSha256([byte[]]$Bytes) {
    $hash = [System.Security.Cryptography.SHA256]::HashData($Bytes)
    return "sha256:" + (-join ($hash | ForEach-Object { $_.ToString("x2") }))
}

function Get-FileSha256([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $hash = [System.Security.Cryptography.SHA256]::HashData($stream)
        return "sha256:" + (-join ($hash | ForEach-Object { $_.ToString("x2") }))
    }
    finally {
        $stream.Dispose()
    }
}

function Get-SkillId([string]$NameZh) {
    return "wikiroco-" + (Get-Sha256Hex $NameZh).Substring(0, 12)
}

function ConvertTo-SkillSourceFingerprint($Skill) {
    return [ordered]@{
        name_zh = [string](Get-JsonProperty $Skill "name_zh")
        element = [string](Get-JsonProperty $Skill "element")
        category = [string](Get-JsonProperty $Skill "category")
        energy = [int](Get-JsonProperty $Skill "energy")
        power = [int](Get-JsonProperty $Skill "power")
        description_zh = [string](Get-JsonProperty $Skill "description_zh")
    }
}

function Get-SourceHash($Skill) {
    $source = ConvertTo-SkillSourceFingerprint $Skill

    return "sha256:" + (Get-Sha256Hex ($source | ConvertTo-Json -Depth 8 -Compress))
}

function Get-SkillIcon($Skill) {
    return Get-JsonProperty $Skill "icon"
}

function Get-SkillIconPath($Skill) {
    return [string](Get-JsonProperty (Get-SkillIcon $Skill) "path")
}

function Get-SkillIconSourceUrl($Skill) {
    return [string](Get-JsonProperty (Get-SkillIcon $Skill) "source_url")
}

function Convert-WikirocoSourceSkill($Source) {
    $nameZh = [string]$Source.name
    $attrZh = [string]$Source.attr
    $typeZh = [string]$Source.type

    if ([string]::IsNullOrWhiteSpace($nameZh)) {
        throw "Wikiroco source skill is missing a name."
    }

    if (!$ElementMap.ContainsKey($attrZh)) {
        throw "Unknown skill element '$attrZh' for '$nameZh'."
    }

    if (!$CategoryMap.ContainsKey($typeZh)) {
        throw "Unknown skill category '$typeZh' for '$nameZh'."
    }

    $id = Get-SkillId $nameZh
    $iconUrl = [string]$Source.icon
    if ([string]::IsNullOrWhiteSpace($iconUrl)) {
        throw "Wikiroco source skill '$nameZh' is missing an icon URL."
    }

    $skill = [ordered]@{
        id = $id
        source_key = [ordered]@{
            provider = "wikiroco"
            name_zh = $nameZh
        }
        name_zh = $nameZh
        aliases_zh = @()
        element = $ElementMap[$attrZh]
        category = $CategoryMap[$typeZh]
        energy = [int]$Source.consume
        power = [int]$Source.power
        description_zh = [string]$Source.desc
        source_hash = $null
        icon = [ordered]@{
            path = "Resources/Skills/$id.png"
            source_url = $iconUrl
            width = 128
            height = 128
            format = "png"
            content_hash = $null
        }
        translations = [ordered]@{
            en = $null
            vi = $null
        }
    }

    $skill.source_hash = Get-SourceHash $skill
    return $skill
}

function Write-SkillNameSample([string]$Label, $Skills) {
    $items = @($Skills)
    if ($items.Count -eq 0) {
        return
    }

    $sample = ($items | Select-Object -First 10 | ForEach-Object { [string](Get-JsonProperty $_ "name_zh") }) -join ", "
    Write-Host "${Label}: $sample"
}

function Get-DatasetHash($Skills) {
    $source = @($Skills | ForEach-Object {
        [ordered]@{
            id = [string](Get-JsonProperty $_ "id")
            source_hash = [string](Get-JsonProperty $_ "source_hash")
            icon = [ordered]@{
                source_url = Get-SkillIconSourceUrl $_
                content_hash = [string](Get-JsonProperty (Get-SkillIcon $_) "content_hash")
            }
        }
    })

    return "sha256:" + (Get-Sha256Hex ($source | ConvertTo-Json -Depth 10 -Compress))
}

function Get-SkillIndex($Skills) {
    $index = @{}
    foreach ($skill in @($Skills)) {
        $id = [string](Get-JsonProperty $skill "id")
        if ([string]::IsNullOrWhiteSpace($id)) {
            throw "Skill record is missing an id."
        }

        if ($index.ContainsKey($id)) {
            throw "Duplicate skill id: $id"
        }

        $index[$id] = $skill
    }

    return $index
}

function Get-WikirocoSkillSnapshot {
    Write-Host "Fetching skills from $SkillApiUrl"
    $skillResponse = Invoke-RestMethod -Uri $SkillApiUrl -TimeoutSec $IconDownloadTimeoutSec
    $sourceSkills = @($skillResponse.items)
    $skills = [System.Collections.Generic.List[object]]::new()
    $seenIds = [System.Collections.Generic.HashSet[string]]::new()

    foreach ($source in $sourceSkills) {
        $skill = Convert-WikirocoSourceSkill $source
        if (!$seenIds.Add([string]$skill.id)) {
            throw "Duplicate generated skill id '$($skill.id)' for '$($skill.name_zh)'."
        }

        $skills.Add($skill)
    }

    $total = [int](Get-JsonProperty $skillResponse "total" $sourceSkills.Count)
    if ($total -ne $sourceSkills.Count) {
        throw "Wikiroco API total ($total) does not match item count ($($sourceSkills.Count))."
    }

    return [pscustomobject]@{
        Total = $total
        Skills = @($skills)
        ById = Get-SkillIndex $skills
    }
}

function Save-JsonFile([string]$Path, $Value) {
    $json = $Value | ConvertTo-Json -Depth 50
    $json = $json -replace "`r`n", "`n"
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $json + "`n", $utf8NoBom)
}

function Test-SourceDescriptionAllowsEmpty([string]$DescriptionZh) {
    return [string]::IsNullOrWhiteSpace($DescriptionZh)
}

function Test-LocalizedDescription([string]$Description, [string]$DescriptionZh) {
    return (Test-SourceDescriptionAllowsEmpty $DescriptionZh) -or -not [string]::IsNullOrWhiteSpace($Description)
}

function Convert-Localization($Localization, [string]$SourceHash, [string]$DescriptionZh) {
    if ($null -eq $Localization) {
        return $null
    }

    $name = [string](Get-JsonProperty $Localization "name" "")
    $description = [string](Get-JsonProperty $Localization "description" "")
    $translatedFromHash = [string](Get-JsonProperty $Localization "translated_from_hash" "")
    $updatedAt = [string](Get-JsonProperty $Localization "updated_at" "")
    if ([string]::IsNullOrWhiteSpace($name) -or
        $null -eq (Get-JsonProperty $Localization "description") -or
        !(Test-LocalizedDescription $description $DescriptionZh)) {
        return $null
    }

    if ($translatedFromHash -ne $SourceHash -or [string]::IsNullOrWhiteSpace($updatedAt)) {
        return $null
    }

    return [ordered]@{
        name = $name
        description = $description
        translated_from_hash = $translatedFromHash
        updated_at = $updatedAt
    }
}

function Convert-LocalizationSet($Localized, [string]$SourceHash, [string]$DescriptionZh) {
    return [ordered]@{
        en = Convert-Localization (Get-JsonProperty $Localized "en") $SourceHash $DescriptionZh
        vi = Convert-Localization (Get-JsonProperty $Localized "vi") $SourceHash $DescriptionZh
    }
}

function New-Localization([string]$Name, [string]$Description, [string]$SourceHash, [string]$UpdatedAt) {
    return [ordered]@{
        name = $Name
        description = $Description
        translated_from_hash = $SourceHash
        updated_at = $UpdatedAt
    }
}

function Test-LocalizationSet($Translations, [string]$SourceHash, [string]$DescriptionZh) {
    $en = Get-JsonProperty $Translations "en"
    $vi = Get-JsonProperty $Translations "vi"
    $enDescription = [string](Get-JsonProperty $en "description" "")
    $viDescription = [string](Get-JsonProperty $vi "description" "")
    return -not [string]::IsNullOrWhiteSpace([string](Get-JsonProperty $en "name" "")) -and
        $null -ne (Get-JsonProperty $en "description") -and
        (Test-LocalizedDescription $enDescription $DescriptionZh) -and
        [string](Get-JsonProperty $en "translated_from_hash" "") -eq $SourceHash -and
        -not [string]::IsNullOrWhiteSpace([string](Get-JsonProperty $en "updated_at" "")) -and
        -not [string]::IsNullOrWhiteSpace([string](Get-JsonProperty $vi "name" "")) -and
        $null -ne (Get-JsonProperty $vi "description") -and
        (Test-LocalizedDescription $viDescription $DescriptionZh) -and
        [string](Get-JsonProperty $vi "translated_from_hash" "") -eq $SourceHash -and
        -not [string]::IsNullOrWhiteSpace([string](Get-JsonProperty $vi "updated_at" ""))
}

function Convert-ImageFileToPngBytes([string]$InputPath, [int]$Width, [int]$Height) {
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

    $memory = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($memory)
        return ,$memory.ToArray()
    }
    finally {
        $memory.Dispose()
    }
}

function Test-PngFile([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $signature = [byte[]]::new(8)
        if ($stream.Read($signature, 0, $signature.Length) -ne $signature.Length) {
            return $false
        }

        $expected = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
        for ($i = 0; $i -lt $expected.Length; $i++) {
            if ($signature[$i] -ne $expected[$i]) {
                return $false
            }
        }

        return $true
    }
    finally {
        $stream.Dispose()
    }
}

function Get-NormalizedImageContentHash([string]$Path, [int]$Width, [int]$Height) {
    $size = Get-ImageSize $Path
    if ($size.Width -eq $Width -and $size.Height -eq $Height -and (Test-PngFile $Path)) {
        return Get-FileSha256 $Path
    }

    return Get-BytesSha256 (Convert-ImageFileToPngBytes $Path $Width $Height)
}

function Test-LocalIconFile([string]$Path, [int]$Width, [int]$Height) {
    if (!(Test-Path $Path)) {
        return [pscustomobject]@{
            Exists = $false
            Valid = $false
            Hash = $null
            Error = "missing"
        }
    }

    try {
        $size = Get-ImageSize $Path
        $isPng = Test-PngFile $Path
        return [pscustomobject]@{
            Exists = $true
            Valid = $isPng -and $size.Width -eq $Width -and $size.Height -eq $Height
            Hash = if ($isPng -and $size.Width -eq $Width -and $size.Height -eq $Height) {
                Get-FileSha256 $Path
            }
            else {
                Get-NormalizedImageContentHash $Path $Width $Height
            }
            Error = $null
        }
    }
    catch {
        return [pscustomobject]@{
            Exists = $true
            Valid = $false
            Hash = $null
            Error = $_.Exception.Message
        }
    }
}

function Get-RemoteIconContentHashes($Skills, [int]$Width, [int]$Height, [int]$ThrottleLimit) {
    $urls = @($Skills |
        ForEach-Object { Get-SkillIconSourceUrl $_ } |
        Where-Object { ![string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique)
    $hashes = @{}
    if ($urls.Count -eq 0) {
        return $hashes
    }

    Write-Host "Checking remote icon content for $($urls.Count) unique URL(s) with throttle $ThrottleLimit"
    $timeoutSec = $IconDownloadTimeoutSec
    $downloads = @($urls | ForEach-Object -ThrottleLimit $ThrottleLimit -Parallel {
        $url = [string]$_
        $client = [System.Net.Http.HttpClient]::new()
        $client.Timeout = [TimeSpan]::FromSeconds($using:timeoutSec)
        try {
            $bytes = $client.GetByteArrayAsync($url).GetAwaiter().GetResult()
            [pscustomobject]@{
                Url = $url
                Bytes = [byte[]]$bytes
                Error = $null
            }
        }
        catch {
            [pscustomobject]@{
                Url = $url
                Bytes = $null
                Error = $_.Exception.Message
            }
        }
        finally {
            $client.Dispose()
        }
    })

    foreach ($download in $downloads) {
        if (![string]::IsNullOrWhiteSpace([string]$download.Error)) {
            throw "Failed to download remote icon '$($download.Url)': $($download.Error)"
        }

        $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) ("peek-image-" + [Guid]::NewGuid().ToString("N"))
        try {
            [System.IO.File]::WriteAllBytes($tempPath, [byte[]]$download.Bytes)
            $hashes[[string]$download.Url] = Get-NormalizedImageContentHash $tempPath $Width $Height
        }
        finally {
            if (Test-Path $tempPath) {
                Remove-Item -LiteralPath $tempPath -Force
            }
        }
    }

    return $hashes
}

function Save-ImageAsPng([string]$InputPath, [string]$OutputPath, [int]$Width, [int]$Height) {
    $bytes = Convert-ImageFileToPngBytes $InputPath $Width $Height
    $directory = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    [System.IO.File]::WriteAllBytes($OutputPath, $bytes)
}

function Save-RemoteImageAsPng([string]$Url, [string]$OutputPath, [int]$Width, [int]$Height) {
    $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) ("peek-image-" + [Guid]::NewGuid().ToString("N"))
    try {
        $bytes = $HttpClient.GetByteArrayAsync($Url).GetAwaiter().GetResult()
        [System.IO.File]::WriteAllBytes($tempPath, $bytes)

        $size = Get-ImageSize $tempPath
        $directory = Split-Path -Parent $OutputPath
        New-Item -ItemType Directory -Force -Path $directory | Out-Null

        if ($size.Width -eq $Width -and $size.Height -eq $Height -and (Test-PngFile $tempPath)) {
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

function New-SkillTranslationSchema {
    return [ordered]@{
        type = "object"
        required = @("skills")
        additionalProperties = $false
        propertyOrdering = @("skills")
        properties = [ordered]@{
            skills = [ordered]@{
                type = "array"
                items = [ordered]@{
                    type = "object"
                    required = @("id", "en", "vi")
                    additionalProperties = $false
                    propertyOrdering = @("id", "en", "vi")
                    properties = [ordered]@{
                        id = [ordered]@{ type = "string" }
                        en = [ordered]@{
                            type = "object"
                            required = @("name", "description")
                            additionalProperties = $false
                            propertyOrdering = @("name", "description")
                            properties = [ordered]@{
                                name = [ordered]@{ type = "string" }
                                description = [ordered]@{ type = "string" }
                            }
                        }
                        vi = [ordered]@{
                            type = "object"
                            required = @("name", "description")
                            additionalProperties = $false
                            propertyOrdering = @("name", "description")
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
}

function New-TranslationMemorySchema {
    return [ordered]@{
        type = "object"
        required = @("style_summary", "english_memory", "vietnamese_memory", "examples")
        additionalProperties = $false
        propertyOrdering = @("style_summary", "english_memory", "vietnamese_memory", "examples")
        properties = [ordered]@{
            style_summary = [ordered]@{ type = "string" }
            english_memory = [ordered]@{ type = "string" }
            vietnamese_memory = [ordered]@{ type = "string" }
            examples = [ordered]@{ type = "string" }
        }
    }
}

function Invoke-GeminiJson(
    [string]$ApiKey,
    [string]$Model,
    [string]$SystemPrompt,
    [string]$UserText,
    $Schema,
    [int]$MaxOutputTokens = 8192) {
    $payload = [ordered]@{
        systemInstruction = [ordered]@{
            parts = @([ordered]@{ text = $SystemPrompt })
        }
        contents = @([ordered]@{
            role = "user"
            parts = @([ordered]@{ text = $UserText })
        })
        safetySettings = $DisabledSafetySettings
        generationConfig = [ordered]@{
            maxOutputTokens = $MaxOutputTokens
            responseMimeType = "application/json"
            responseJsonSchema = $Schema
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
                -Body ($payload | ConvertTo-Json -Depth 60 -Compress) `
                -TimeoutSec 120
            $text = $response.candidates[0].content.parts[0].text
            if ([string]::IsNullOrWhiteSpace($text)) {
                throw "Empty Gemini response."
            }

            return ($text | ConvertFrom-Json)
        }
        catch {
            if ($attempt -eq 3) {
                throw
            }

            Start-Sleep -Seconds (2 * $attempt)
        }
    }
}

function ConvertTo-TranslationMemoryInput($Skills) {
    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($skill in @($Skills)) {
        $sourceHash = [string](Get-JsonProperty $skill "source_hash" "")
        $descriptionZh = [string](Get-JsonProperty $skill "description_zh" "")
        if (!(Test-LocalizationSet (Get-JsonProperty $skill "translations") $sourceHash $descriptionZh)) {
            continue
        }

        $translations = Get-JsonProperty $skill "translations"
        $en = Get-JsonProperty $translations "en"
        $vi = Get-JsonProperty $translations "vi"
        $items.Add([ordered]@{
            id = [string](Get-JsonProperty $skill "id")
            name_zh = [string](Get-JsonProperty $skill "name_zh")
            description_zh = $descriptionZh
            element = [string](Get-JsonProperty $skill "element")
            category = [string](Get-JsonProperty $skill "category")
            en = [ordered]@{
                name = [string](Get-JsonProperty $en "name")
                description = [string](Get-JsonProperty $en "description")
            }
            vi = [ordered]@{
                name = [string](Get-JsonProperty $vi "name")
                description = [string](Get-JsonProperty $vi "description")
            }
        }) | Out-Null
    }

    return @($items)
}

function ConvertTo-TranslationWorkItems($Skills) {
    return @($Skills | ForEach-Object {
        [ordered]@{
            id = [string](Get-JsonProperty $_ "id")
            name_zh = [string](Get-JsonProperty $_ "name_zh")
            description_zh = [string](Get-JsonProperty $_ "description_zh")
            element = [string](Get-JsonProperty $_ "element")
            category = [string](Get-JsonProperty $_ "category")
        }
    })
}

function ConvertTo-HarmonizationWorkItems($Skills) {
    return @($Skills | ForEach-Object {
        $translations = Get-JsonProperty $_ "translations"
        $en = Get-JsonProperty $translations "en"
        $vi = Get-JsonProperty $translations "vi"
        [ordered]@{
            id = [string](Get-JsonProperty $_ "id")
            name_zh = [string](Get-JsonProperty $_ "name_zh")
            description_zh = [string](Get-JsonProperty $_ "description_zh")
            element = [string](Get-JsonProperty $_ "element")
            category = [string](Get-JsonProperty $_ "category")
            en = [ordered]@{
                name = [string](Get-JsonProperty $en "name")
                description = [string](Get-JsonProperty $en "description")
            }
            vi = [ordered]@{
                name = [string](Get-JsonProperty $vi "name")
                description = [string](Get-JsonProperty $vi "description")
            }
        }
    })
}

function Invoke-TranslationMemoryFromText([string]$UserText, [string]$ApiKey, [string]$Model, [string]$Scope) {
    $systemPrompt = @"
You are the translation editor for the Roco Kingdom: World skill database.
Build a compact translation memory from the provided Chinese, English, and Vietnamese $Scope.
This is not a hard rulebook. Infer the best recurring terminology, tone, and examples from the corpus, resolve obvious inconsistency by choosing the strongest player-facing wording, and keep the memory concise enough to reuse in future translation prompts.
"@

    return Invoke-GeminiJson `
        $ApiKey `
        $Model `
        $systemPrompt `
        $UserText `
        (New-TranslationMemorySchema) `
        8192
}

function Invoke-TranslationMemoryBuild($Skills, [string]$ApiKey, [string]$Model) {
    $items = ConvertTo-TranslationMemoryInput $Skills
    if ($items.Count -eq 0) {
        throw "No validated existing translations are available to build translation memory."
    }

    $partialMemories = [System.Collections.Generic.List[object]]::new()
    if ($items.Count -le $TranslationMemoryBatchSize) {
        $partialMemories.Add((Invoke-TranslationMemoryFromText `
            ("Build translation memory from this corpus: " + ($items | ConvertTo-Json -Depth 20 -Compress)) `
            $ApiKey `
            $Model `
            "corpus")) | Out-Null
    }
    else {
        $totalBatches = [Math]::Ceiling($items.Count / $TranslationMemoryBatchSize)
        for ($i = 0; $i -lt $items.Count; $i += $TranslationMemoryBatchSize) {
            $end = [Math]::Min($i + $TranslationMemoryBatchSize - 1, $items.Count - 1)
            $batch = @($items[$i..$end])
            $batchNumber = [Math]::Floor($i / $TranslationMemoryBatchSize) + 1
            Write-Host "Building translation memory slice $batchNumber/$totalBatches ($($batch.Count) skill(s))"
            $partialMemories.Add((Invoke-TranslationMemoryFromText `
                ("Build translation memory from this corpus slice: " + ($batch | ConvertTo-Json -Depth 20 -Compress)) `
                $ApiKey `
                $Model `
                "corpus slice")) | Out-Null
        }
    }

    $memory = if ($partialMemories.Count -eq 1) {
        $partialMemories[0]
    }
    else {
        Write-Host "Merging $($partialMemories.Count) translation memory slice(s)"
        Invoke-TranslationMemoryFromText `
            ("Merge these translation memories into one compact corpus memory: " + (@($partialMemories) | ConvertTo-Json -Depth 60 -Compress)) `
            $ApiKey `
            $Model `
            "partial translation memories"
    }

    $output = [ordered]@{
        schema_version = 1
        generated_at = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", [Globalization.CultureInfo]::InvariantCulture)
        model = $Model
        source_dataset_hash = Get-DatasetHash $Skills
        memory = $memory
    }

    $directory = Split-Path -Parent $TranslationMemoryPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    Save-JsonFile $TranslationMemoryPath $output
    Write-Host "Translation memory written: $TranslationMemoryPath"
    return $output
}

function Get-TranslationMemory {
    if (!(Test-Path $TranslationMemoryPath)) {
        return $null
    }

    return Get-Content -LiteralPath $TranslationMemoryPath -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Ensure-TranslationMemory($Skills, [string]$ApiKey, [string]$Model, [bool]$Force) {
    if ($Force -or !(Test-Path $TranslationMemoryPath)) {
        return Invoke-TranslationMemoryBuild $Skills $ApiKey $Model
    }

    Write-Host "Using translation memory: $TranslationMemoryPath"
    return Get-TranslationMemory
}

function Get-TranslationMemoryPromptText($TranslationMemory) {
    if ($null -eq $TranslationMemory) {
        return "No translation memory is available."
    }

    return ($TranslationMemory | ConvertTo-Json -Depth 60 -Compress)
}

function Test-TranslationRows($Rows, $Items, [string]$Context) {
    $rows = @($Rows)
    $items = @($Items)
    if ($rows.Count -ne $items.Count) {
        throw "Expected $($items.Count) $Context skills, got $($rows.Count)."
    }

    $expectedItems = @{}
    foreach ($item in $items) {
        $expectedItems[[string](Get-JsonProperty $item "id")] = $item
    }

    $seenIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($row in $rows) {
        $id = [string](Get-JsonProperty $row "id")
        if (!$expectedItems.ContainsKey($id)) {
            throw "Gemini returned unexpected skill id '$id'."
        }

        if (!$seenIds.Add($id)) {
            throw "Gemini returned duplicate skill id '$id'."
        }

        $sourceDescription = [string](Get-JsonProperty $expectedItems[$id] "description_zh" "")
        foreach ($locale in @("en", "vi")) {
            $localization = Get-JsonProperty $row $locale
            $description = [string](Get-JsonProperty $localization "description" "")
            if ([string]::IsNullOrWhiteSpace([string](Get-JsonProperty $localization "name" "")) -or
                $null -eq (Get-JsonProperty $localization "description") -or
                !(Test-LocalizedDescription $description $sourceDescription)) {
                throw "Gemini returned invalid $locale localization for '$id'."
            }
        }
    }
}

function Invoke-SkillTranslationBatch($Skills, [string]$ApiKey, [string]$Model, $TranslationMemory) {
    $items = ConvertTo-TranslationWorkItems $Skills
    $systemPrompt = @"
Translate Roco Kingdom: World skill names and descriptions from Simplified Chinese to English and Vietnamese.
Use the translation memory as the corpus style reference: consistent terms, natural game UI wording, and concise player-facing phrasing.
Return every requested id exactly once. Preserve source meaning, numbers, signs, and effect structure. If the Chinese description is empty, return an empty description.
"@

    $userText = "Translation memory: $(Get-TranslationMemoryPromptText $TranslationMemory)`nSkills to translate: " +
        ($items | ConvertTo-Json -Depth 20 -Compress)
    $result = Invoke-GeminiJson $ApiKey $Model $systemPrompt $userText (New-SkillTranslationSchema) 8192
    $rows = @($result.skills)
    Test-TranslationRows $rows $items "translated"
    return $rows
}

function Invoke-SkillTranslationHarmonizationBatch($Skills, [string]$ApiKey, [string]$Model, $TranslationMemory) {
    $items = ConvertTo-HarmonizationWorkItems $Skills
    $systemPrompt = @"
You are the final consistency editor for Roco Kingdom: World skill translations.
Use the translation memory to harmonize terminology and phrasing across English and Vietnamese.
Make focused edits only when they improve consistency, naturalness, or fidelity. Return every id exactly once, including unchanged rows. If the Chinese description is empty, keep the translated descriptions empty.
"@

    $userText = "Translation memory: $(Get-TranslationMemoryPromptText $TranslationMemory)`nDraft translations to harmonize: " +
        ($items | ConvertTo-Json -Depth 20 -Compress)
    $result = Invoke-GeminiJson $ApiKey $Model $systemPrompt $userText (New-SkillTranslationSchema) 8192
    $rows = @($result.skills)
    Test-TranslationRows $rows $items "harmonized"
    return $rows
}

function Set-SkillTranslationsFromRows($Skills, $Rows, [bool]$Force) {
    $rowById = @{}
    foreach ($row in @($Rows)) {
        $rowById[[string](Get-JsonProperty $row "id")] = $row
    }

    $updatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", [Globalization.CultureInfo]::InvariantCulture)
    $updatedCount = 0
    foreach ($skill in @($Skills)) {
        $id = [string](Get-JsonProperty $skill "id")
        if (!$rowById.ContainsKey($id)) {
            continue
        }

        $row = $rowById[$id]
        $newEn = Get-JsonProperty $row "en"
        $newVi = Get-JsonProperty $row "vi"
        $translations = Get-JsonProperty $skill "translations"
        $currentEn = Get-JsonProperty $translations "en"
        $currentVi = Get-JsonProperty $translations "vi"
        $changed = [string](Get-JsonProperty $currentEn "name" "") -ne [string](Get-JsonProperty $newEn "name" "") -or
            [string](Get-JsonProperty $currentEn "description" "") -ne [string](Get-JsonProperty $newEn "description" "") -or
            [string](Get-JsonProperty $currentVi "name" "") -ne [string](Get-JsonProperty $newVi "name" "") -or
            [string](Get-JsonProperty $currentVi "description" "") -ne [string](Get-JsonProperty $newVi "description" "")

        if (!$Force -and !$changed) {
            continue
        }

        $sourceHash = [string](Get-JsonProperty $skill "source_hash")
        $skill.translations = [ordered]@{
            en = New-Localization ([string](Get-JsonProperty $newEn "name")) ([string](Get-JsonProperty $newEn "description")) $sourceHash $updatedAt
            vi = New-Localization ([string](Get-JsonProperty $newVi "name")) ([string](Get-JsonProperty $newVi "description")) $sourceHash $updatedAt
        }
        $updatedCount++
    }

    return $updatedCount
}

function Test-SkillDatabase([string]$Path, [bool]$RequireTranslations, [bool]$RequireIconFiles = $true) {
    $data = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    $skills = @($data.skills)
    $errors = [System.Collections.Generic.List[string]]::new()

    if ([int](Get-JsonProperty $data "schema_version" 0) -ne 2) {
        $errors.Add("schema_version must be 2.")
    }

    $source = Get-JsonProperty $data "source"
    if ([string](Get-JsonProperty $source "provider" "") -ne "wikiroco") {
        $errors.Add("source.provider must be wikiroco.")
    }

    if ([string](Get-JsonProperty $source "url" "") -ne $SkillApiUrl) {
        $errors.Add("source.url must be $SkillApiUrl.")
    }

    $fetchedAtValue = Get-JsonProperty $source "fetched_at" ""
    $fetchedAt = if ($fetchedAtValue -is [DateTime]) {
        $fetchedAtValue.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", [Globalization.CultureInfo]::InvariantCulture)
    }
    else {
        [string]$fetchedAtValue
    }
    if ([string]::IsNullOrWhiteSpace($fetchedAt)) {
        $errors.Add("source.fetched_at is required.")
    }
    elseif ($fetchedAt -notmatch "^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$") {
        $errors.Add("source.fetched_at must be a UTC timestamp like yyyy-MM-ddTHH:mm:ssZ.")
    }
    else {
        try {
            [void][DateTimeOffset]::ParseExact(
                $fetchedAt,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal)
        }
        catch {
            $errors.Add("source.fetched_at must be a valid UTC timestamp.")
        }
    }

    if ([int](Get-JsonProperty $source "source_count" 0) -lt $skills.Count) {
        $errors.Add("source.source_count cannot be less than skills array count.")
    }

    if ($skills.Count -ne [int](Get-JsonProperty $source "item_count" 0)) {
        $errors.Add("source.item_count does not match skills array count.")
    }

    $datasetHash = [string](Get-JsonProperty $source "dataset_hash" "")
    if ($datasetHash -notmatch "^sha256:[0-9a-f]{64}$") {
        $errors.Add("source.dataset_hash is invalid.")
    }
    elseif ($datasetHash -ne (Get-DatasetHash $skills)) {
        $errors.Add("source.dataset_hash does not match skills.")
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
        $id = [string](Get-JsonProperty $skill "id" "")
        $nameZh = [string](Get-JsonProperty $skill "name_zh" "")
        if ([string]::IsNullOrWhiteSpace($id)) {
            $errors.Add("Skill is missing an id.")
            continue
        }

        if ([string]::IsNullOrWhiteSpace($nameZh)) {
            $errors.Add("Skill is missing name_zh: $id")
        }

        if (@($ElementMap.Values) -notcontains [string](Get-JsonProperty $skill "element" "")) {
            $errors.Add("Invalid element: $id $($skill.element)")
        }

        if (@($CategoryMap.Values) -notcontains [string](Get-JsonProperty $skill "category" "")) {
            $errors.Add("Invalid category: $id $($skill.category)")
        }

        try {
            [void][int](Get-JsonProperty $skill "energy")
            [void][int](Get-JsonProperty $skill "power")
        }
        catch {
            $errors.Add("Skill energy and power must be integers: $id")
        }

        $expectedId = Get-SkillId $nameZh
        if ($id -ne $expectedId) {
            $errors.Add("Skill id mismatch: $id should be $expectedId for $nameZh")
        }

        $sourceKey = Get-JsonProperty $skill "source_key"
        if ([string](Get-JsonProperty $sourceKey "provider" "") -ne "wikiroco" -or
            [string](Get-JsonProperty $sourceKey "name_zh" "") -ne $nameZh) {
            $errors.Add("source_key mismatch: $id")
        }

        $expectedIcon = "Resources/Skills/$id.png"
        $iconInfo = Get-SkillIcon $skill
        if ([string](Get-JsonProperty $iconInfo "path" "") -ne $expectedIcon) {
            $errors.Add("Skill icon path mismatch: $id should use $expectedIcon")
        }

        $sourceHash = [string](Get-JsonProperty $skill "source_hash" "")
        if ($sourceHash -notmatch "^sha256:[0-9a-f]{64}$") {
            $errors.Add("Invalid source_hash: $id")
        }
        else {
            try {
                $expectedHash = Get-SourceHash $skill
                if ($sourceHash -ne $expectedHash) {
                    $errors.Add("source_hash mismatch: $id should be $expectedHash")
                }
            }
            catch {
                $errors.Add("Could not compute source_hash: $id $($_.Exception.Message)")
            }
        }

        if ([string]::IsNullOrWhiteSpace([string](Get-JsonProperty $iconInfo "source_url" ""))) {
            $errors.Add("Missing icon.source_url: $id")
        }

        if ([int](Get-JsonProperty $iconInfo "width" 0) -ne 128 -or
            [int](Get-JsonProperty $iconInfo "height" 0) -ne 128 -or
            [string](Get-JsonProperty $iconInfo "format" "") -ne "png") {
            $errors.Add("Invalid icon metadata: $id")
        }

        $iconContentHash = [string](Get-JsonProperty $iconInfo "content_hash" "")
        if ($iconContentHash -notmatch "^sha256:[0-9a-f]{64}$") {
            $errors.Add("Invalid icon.content_hash: $id")
        }

        if ($RequireIconFiles) {
            $icon = Join-Path $RepoRoot (Get-SkillIconPath $skill)
            if (!(Test-Path $icon)) {
                $errors.Add("Missing skill icon: $($skill.id) -> $(Get-SkillIconPath $skill)")
                continue
            }

            if ($iconContentHash -match "^sha256:[0-9a-f]{64}$" -and $iconContentHash -ne (Get-FileSha256 $icon)) {
                $errors.Add("icon.content_hash mismatch: $id")
            }

            if (!(Test-PngFile $icon)) {
                $errors.Add("Skill icon must be a PNG file: $(Get-SkillIconPath $skill)")
            }

            $size = Get-ImageSize $icon
            if ($size.Width -ne 128 -or $size.Height -ne 128) {
                $errors.Add("Skill icon must be 128x128: $(Get-SkillIconPath $skill) is $($size.Width)x$($size.Height)")
            }
        }

        if ($RequireTranslations -and !(Test-LocalizationSet $skill.translations $sourceHash ([string](Get-JsonProperty $skill "description_zh" "")))) {
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

    if ($RequireIconFiles) {
        Write-Host "Validation passed: $($skills.Count) skills, all icons present, all skill icons 128x128."
    }
    else {
        Write-Host "Structural validation passed: $($skills.Count) skills."
    }
}

function Test-WikirocoFreshness([string]$Path, [bool]$CheckIconContent) {
    Test-SkillDatabase $Path (-not $AllowMissingTranslations)

    $existingData = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    $existingSkills = @($existingData.skills)
    $existingById = Get-SkillIndex $existingSkills
    $snapshot = Get-WikirocoSkillSnapshot
    $remoteIconHashes = if ($CheckIconContent) {
        Get-RemoteIconContentHashes $snapshot.Skills 128 128 $IconCheckThrottle
    }
    else {
        @{}
    }

    $newSkills = [System.Collections.Generic.List[object]]::new()
    $changedSkills = [System.Collections.Generic.List[object]]::new()
    $iconUrlChangedSkills = [System.Collections.Generic.List[object]]::new()
    $iconContentChangedSkills = [System.Collections.Generic.List[object]]::new()
    $removedSkills = [System.Collections.Generic.List[object]]::new()

    foreach ($skill in @($snapshot.Skills)) {
        if (!$existingById.ContainsKey([string]$skill.id)) {
            $newSkills.Add($skill)
            continue
        }

        $existing = $existingById[[string]$skill.id]
        if ([string](Get-JsonProperty $existing "source_hash" "") -ne [string]$skill.source_hash) {
            $changedSkills.Add($skill)
        }

        $existingIconUrl = Get-SkillIconSourceUrl $existing
        $remoteIconUrl = Get-SkillIconSourceUrl $skill
        if ($existingIconUrl -ne $remoteIconUrl) {
            $iconUrlChangedSkills.Add($skill)
        }
        elseif ($CheckIconContent) {
            $existingIconHash = [string](Get-JsonProperty (Get-SkillIcon $existing) "content_hash" "")
            $remoteIconHash = [string]$remoteIconHashes[$remoteIconUrl]
            if ($existingIconHash -ne $remoteIconHash) {
                $iconContentChangedSkills.Add($skill)
            }
        }
    }

    foreach ($existing in $existingSkills) {
        if (!$snapshot.ById.ContainsKey([string]$existing.id)) {
            $removedSkills.Add($existing)
        }
    }

    $existingSource = Get-JsonProperty $existingData "source"
    $countChanged = [int](Get-JsonProperty $existingSource "source_count" 0) -ne $snapshot.Total -or
        [int](Get-JsonProperty $existingSource "item_count" 0) -ne $snapshot.Skills.Count -or
        $existingSkills.Count -ne $snapshot.Skills.Count
    $needsUpdate = $countChanged -or
        $newSkills.Count -gt 0 -or
        $changedSkills.Count -gt 0 -or
        $iconUrlChangedSkills.Count -gt 0 -or
        $iconContentChangedSkills.Count -gt 0 -or
        $removedSkills.Count -gt 0

    $fetchedAtValue = Get-JsonProperty $existingSource "fetched_at" ""
    $fetchedAt = if ($fetchedAtValue -is [DateTime]) {
        $fetchedAtValue.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", [Globalization.CultureInfo]::InvariantCulture)
    }
    else {
        [string]$fetchedAtValue
    }
    Write-Host "Local fetched at: $fetchedAt"
    Write-Host "Local count: $($existingSkills.Count)"
    Write-Host "Remote total: $($snapshot.Total)"
    Write-Host "Remote items: $($snapshot.Skills.Count)"
    Write-Host "New: $($newSkills.Count)"
    Write-Host "Changed: $($changedSkills.Count)"
    Write-Host "Icon URL changed: $($iconUrlChangedSkills.Count)"
    if ($CheckIconContent) {
        Write-Host "Icon content changed: $($iconContentChangedSkills.Count)"
    }
    else {
        Write-Host "Icon content changed: skipped (use -DeepIcons)"
    }
    Write-Host "Removed: $($removedSkills.Count)"

    Write-SkillNameSample "New skills" $newSkills
    Write-SkillNameSample "Changed skills" $changedSkills
    Write-SkillNameSample "Icon URL changes" $iconUrlChangedSkills
    Write-SkillNameSample "Icon content changes" $iconContentChangedSkills
    Write-SkillNameSample "Removed skills" $removedSkills

    if ($needsUpdate) {
        Write-Warning "Bundled wikiroco data is not current. Run tools\update-skills.ps1 to update it."
        return $false
    }

    Write-Host "Upstream freshness check passed: bundled wikiroco data is current."
    return $true
}

function Save-SkillDataWithBackup($Output, [bool]$RequireTranslations) {
    $backupDir = Join-Path $RepoRoot "data\skill-backups"
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    $backupPath = Join-Path $backupDir ("skills.json.bak-{0}" -f (Get-Date -Format yyyyMMddHHmmss))
    Copy-Item -LiteralPath $DataPath -Destination $backupPath
    Write-Host "Backup written: $backupPath"

    $tempDataPath = Join-Path ([System.IO.Path]::GetTempPath()) ("peek-skills-" + [Guid]::NewGuid().ToString("N") + ".json")
    try {
        Save-JsonFile $tempDataPath $Output
        Test-SkillDatabase $tempDataPath $RequireTranslations
        Move-Item -LiteralPath $tempDataPath -Destination $DataPath -Force
    }
    finally {
        if (Test-Path $tempDataPath) {
            Remove-Item -LiteralPath $tempDataPath -Force
        }
    }

    Write-Host "Skill database updated: $DataPath"
}

if ($ValidateOnly -and $CheckFreshness) {
    throw "Use either -ValidateOnly or -CheckFreshness, not both."
}

if ($ValidateOnly -and $DeepIcons) {
    throw "Use -DeepIcons with -CheckFreshness or the update path, not -ValidateOnly."
}

if ($ValidateOnly) {
    Test-SkillDatabase $DataPath (-not $AllowMissingTranslations)
    return
}

if ($CheckFreshness) {
    if (Test-WikirocoFreshness $DataPath $DeepIcons) {
        return
    }

    exit 1
}

if ($BuildTranslationMemory -and !$TranslateChanged -and !$HarmonizeTranslations) {
    if ([string]::IsNullOrWhiteSpace($GeminiApiKey)) {
        throw "Gemini API key is required. Set GEMINI_API_KEY or pass -GeminiApiKey."
    }

    Test-SkillDatabase $DataPath (-not $AllowMissingTranslations)
    $existingData = Get-Content -LiteralPath $DataPath -Raw -Encoding UTF8 | ConvertFrom-Json
    [void](Ensure-TranslationMemory @($existingData.skills) $GeminiApiKey $GeminiModel $true)
    return
}

if ($HarmonizeTranslations -and !$TranslateChanged) {
    if ([string]::IsNullOrWhiteSpace($GeminiApiKey)) {
        throw "Gemini API key is required. Set GEMINI_API_KEY or pass -GeminiApiKey."
    }

    Test-SkillDatabase $DataPath $true
    $existingData = Get-Content -LiteralPath $DataPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $existingSkills = @($existingData.skills)
    $translationMemory = Ensure-TranslationMemory $existingSkills $GeminiApiKey $GeminiModel $BuildTranslationMemory
    $totalBatches = [Math]::Ceiling($existingSkills.Count / $HarmonizationBatchSize)
    $updatedCount = 0
    for ($i = 0; $i -lt $existingSkills.Count; $i += $HarmonizationBatchSize) {
        $end = [Math]::Min($i + $HarmonizationBatchSize - 1, $existingSkills.Count - 1)
        $batch = @($existingSkills[$i..$end])
        $batchNumber = [Math]::Floor($i / $HarmonizationBatchSize) + 1
        Write-Host "Harmonizing batch $batchNumber/$totalBatches ($($batch.Count) skill(s))"
        $rows = Invoke-SkillTranslationHarmonizationBatch $batch $GeminiApiKey $GeminiModel $translationMemory
        $updatedCount += Set-SkillTranslationsFromRows $batch $rows $false
    }

    Write-Host "Harmonization updated $updatedCount skill translation(s)."
    if ($updatedCount -gt 0) {
        Save-SkillDataWithBackup $existingData $true
    }

    return
}

Test-SkillDatabase $DataPath $false $false

$existingData = Get-Content -LiteralPath $DataPath -Raw -Encoding UTF8 | ConvertFrom-Json
$existingById = Get-SkillIndex @($existingData.skills)
$snapshot = Get-WikirocoSkillSnapshot

$newSkills = [System.Collections.Generic.List[object]]::new()
$translationQueue = [System.Collections.Generic.List[object]]::new()

foreach ($skill in @($snapshot.Skills)) {
    $id = [string]$skill.id

    $existing = $null
    if ($existingById.ContainsKey($id)) {
        $existing = $existingById[$id]
    }

    $oldHash = [string](Get-JsonProperty $existing "source_hash" "")
    $descriptionZh = [string](Get-JsonProperty $skill "description_zh" "")
    if ($null -ne $existing -and $oldHash -eq $skill.source_hash -and (Test-LocalizationSet $existing.translations $skill.source_hash $descriptionZh)) {
        $skill.translations = Convert-LocalizationSet $existing.translations $skill.source_hash $descriptionZh
    }
    else {
        $translationQueue.Add($skill)
    }

    $newSkills.Add($skill)
}

if ($translationQueue.Count -gt 0 -and !$TranslateChanged -and !$AllowMissingTranslations) {
    throw "$($translationQueue.Count) skill(s) are new or changed and need translations. Re-run with -TranslateChanged, or pass -AllowMissingTranslations for a non-release data file."
}

if ($TranslateChanged -and $translationQueue.Count -gt 0) {
    if ([string]::IsNullOrWhiteSpace($GeminiApiKey)) {
        throw "Gemini API key is required. Set GEMINI_API_KEY or pass -GeminiApiKey."
    }

    $translationMemory = Ensure-TranslationMemory @($existingData.skills) $GeminiApiKey $GeminiModel $BuildTranslationMemory
    $totalBatches = [Math]::Ceiling($translationQueue.Count / $TranslationBatchSize)
    $draftRows = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $translationQueue.Count; $i += $TranslationBatchSize) {
        $end = [Math]::Min($i + $TranslationBatchSize - 1, $translationQueue.Count - 1)
        $batch = @($translationQueue[$i..$end])
        $batchNumber = [Math]::Floor($i / $TranslationBatchSize) + 1
        Write-Host "Translating batch $batchNumber/$totalBatches ($($batch.Count) skill(s))"
        $rows = Invoke-SkillTranslationBatch $batch $GeminiApiKey $GeminiModel $translationMemory
        foreach ($row in $rows) {
            $draftRows.Add($row) | Out-Null
        }
    }

    [void](Set-SkillTranslationsFromRows @($translationQueue) @($draftRows) $true)

    if (!$SkipHarmonization) {
        $totalHarmonizationBatches = [Math]::Ceiling($translationQueue.Count / $HarmonizationBatchSize)
        for ($i = 0; $i -lt $translationQueue.Count; $i += $HarmonizationBatchSize) {
            $end = [Math]::Min($i + $HarmonizationBatchSize - 1, $translationQueue.Count - 1)
            $batch = @($translationQueue[$i..$end])
            $batchNumber = [Math]::Floor($i / $HarmonizationBatchSize) + 1
            Write-Host "Harmonizing translated batch $batchNumber/$totalHarmonizationBatches ($($batch.Count) skill(s))"
            $rows = Invoke-SkillTranslationHarmonizationBatch $batch $GeminiApiKey $GeminiModel $translationMemory
            [void](Set-SkillTranslationsFromRows $batch $rows $true)
        }
    }
}

New-Item -ItemType Directory -Force -Path $SkillIconDir, $ElementIconDir, $SkillMetaIconDir | Out-Null
Update-RocomwikiIconResources $RefreshIcons

$remoteIconHashes = if ($DeepIcons) {
    Get-RemoteIconContentHashes $newSkills 128 128 $IconCheckThrottle
}
else {
    @{}
}

foreach ($skill in @($newSkills)) {
    $id = [string]$skill.id
    $iconPath = Join-Path $RepoRoot (Get-SkillIconPath $skill)
    $iconUrl = Get-SkillIconSourceUrl $skill

    $existing = $null
    if ($existingById.ContainsKey($id)) {
        $existing = $existingById[$id]
    }

    $oldIconUrl = if ($null -eq $existing) { "" } else { Get-SkillIconSourceUrl $existing }
    $oldIconHash = if ($null -eq $existing) { "" } else { [string](Get-JsonProperty (Get-SkillIcon $existing) "content_hash" "") }
    $localIconState = Test-LocalIconFile $iconPath 128 128
    $localIconHash = [string]$localIconState.Hash
    $remoteIconHash = if ($DeepIcons) { [string]$remoteIconHashes[$iconUrl] } else { $null }
    $shouldDownloadIcon = $RefreshIcons -or
        !$localIconState.Exists -or
        !$localIconState.Valid -or
        (![string]::IsNullOrWhiteSpace($oldIconUrl) -and $oldIconUrl -ne $iconUrl) -or
        (![string]::IsNullOrWhiteSpace($oldIconHash) -and ![string]::IsNullOrWhiteSpace($localIconHash) -and $oldIconHash -ne $localIconHash) -or
        ($DeepIcons -and $remoteIconHash -ne $localIconHash)

    if ($shouldDownloadIcon) {
        Save-RemoteImageAsPng $iconUrl $iconPath 128 128
    }

    $skill.icon.content_hash = if ($DeepIcons -and !$shouldDownloadIcon) {
        $remoteIconHash
    }
    else {
        Get-FileSha256 $iconPath
    }
}

$fetchedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", [Globalization.CultureInfo]::InvariantCulture)
$output = [ordered]@{
    schema_version = 2
    source = [ordered]@{
        provider = "wikiroco"
        url = $SkillApiUrl
        fetched_at = $fetchedAt
        source_count = $snapshot.Total
        item_count = $snapshot.Skills.Count
        dataset_hash = Get-DatasetHash $newSkills
    }
    skills = @($newSkills)
}

Save-SkillDataWithBackup $output (-not $AllowMissingTranslations)
