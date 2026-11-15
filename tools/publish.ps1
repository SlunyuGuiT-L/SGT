# Omni 在线模块发布脚本
#
#   .\publish.ps1            体检：字段、文件是否存在、SHA256 是否与清单一致
#   .\publish.ps1 -Check     编译 .cs 模块（用宿主的 DLL）并跑模块契约预检
#   .\publish.ps1 -Update    重算 SHA256 写回 TreeHouseModules.json
#
# 三个可以一起用，例如推 GitHub 前：.\publish.ps1 -Check -Update

[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$Update,
    [string]$HostDir,
    [string]$DalamudDevDir = (Join-Path $env:AppData 'XIVLauncherCN\addon\Hooks\dev'),
    [string]$ModuleCheckDll = 'E:\dshku\refs\_check\bin\Release\net10.0-windows\ModuleCheck.dll'
)

$ErrorActionPreference = 'Stop'
$repoRoot    = Split-Path $PSScriptRoot -Parent
$manifestPath = Join-Path $repoRoot 'TreeHouseModules.json'
$problems    = [System.Collections.Generic.List[string]]::new()

function Resolve-HostDir {
    if ($HostDir) { return (Resolve-Path $HostDir).Path }
    $root = Join-Path $env:AppData 'XIVLauncherCN\installedPlugins\OmniToolbox'
    if (-not (Test-Path $root)) { throw "找不到已安装的 OmniToolbox：$root" }
    $latest = Get-ChildItem $root -Directory |
        Sort-Object { [version]($_.Name -replace '[^0-9.]', '') } -Descending |
        Select-Object -First 1
    return $latest.FullName
}

# ---------- 读清单 ----------

if (-not (Test-Path $manifestPath)) { throw "找不到清单：$manifestPath" }

# 读清单（显式 UTF-8，避免 5.1 下 Get-Content 按 ANSI 解码把中文读成乱码）
$utf8NoBom    = New-Object System.Text.UTF8Encoding($false)
$manifestText = [System.IO.File]::ReadAllText($manifestPath)
$manifest     = $manifestText | ConvertFrom-Json

if ($manifest.SchemaVersion -ne 1) { $problems.Add("SchemaVersion 必须是 1（当前 $($manifest.SchemaVersion)）") }

$hostPath = Resolve-HostDir
Write-Host "宿主目录 : $hostPath"

# ---------- 逐模块校验 ----------

foreach ($module in $manifest.Modules)
{
    $name = $module.InternalName
    Write-Host "`n== $name =="

    foreach ($required in 'InternalName', 'Author', 'RepositoryURL', 'Version', 'ApiVersion', 'MinimumOmniVersion', 'File', 'Sha256')
    {
        if (-not $module.$required) { $problems.Add("$name 缺少字段 $required") }
    }

    if ($module.ApiVersion -ne 1) { $problems.Add("$name 的 ApiVersion 必须是 1") }

    $minimum = [version]$module.MinimumOmniVersion
    if ($minimum -lt [version]'1.1.8.2') { $problems.Add("$name 的 MinimumOmniVersion 低于 1.1.8.2，旧接口模块会被拒载") }

    if ($module.RepositoryURL -notmatch '^https://github\.com/[^/]+/[^/]+$')
    {
        $problems.Add("$name 的 RepositoryURL 必须是 https://github.com/owner/repo 形式")
    }

    $filePath = Join-Path $repoRoot $module.File
    if (-not (Test-Path $filePath)) { $problems.Add("$name 的文件不存在：$($module.File)"); continue }

    $fileName = [System.IO.Path]::GetFileNameWithoutExtension($filePath)
    if ($fileName -ne $name) { $problems.Add("$name 的文件名必须与 InternalName 一致（当前 $fileName）") }

    $hash = (Get-FileHash $filePath -Algorithm SHA256).Hash
    $match = $hash -eq ($module.Sha256).ToUpperInvariant()
    $note = if ($match) { 'OK' } elseif ($Update) { '将写回清单' } else { '与清单不一致' }
    Write-Host ("  SHA256   : {0}  {1}" -f $hash, $note)
    if (-not $match -and -not $Update) { $problems.Add("$name 的 SHA256 需要更新（用 -Update）") }

    # ---------- 编译 + 契约预检 ----------

    if ($Check -and $filePath.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase))
    {
        if (-not (Test-Path $ModuleCheckDll))
        {
            Write-Host "  跳过契约预检：找不到 ModuleCheck（$ModuleCheckDll）" -ForegroundColor Yellow
            continue
        }

        $buildDir = Join-Path $repoRoot ("tools\.build\" + $name)
        New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

        $references = @()
        foreach ($dllName in 'OmniToolbox', 'OmniToolbox.Common', 'TreeTools', 'TreeToolKit', 'OmenTools', 'KamiToolKit')
        {
            $candidate = Join-Path $hostPath "$dllName.dll"
            if (Test-Path $candidate) { $references += $candidate }
        }
        $references += (Get-ChildItem $DalamudDevDir -Filter *.dll |
            Where-Object { $_.Name -notmatch 'cimgui|cimguizmo|cimplot|nethost|e_sqlite3|Dalamud\.Boot|Dalamud\.Injector|DalamudCrashHandler' }).FullName

        $sb = [System.Text.StringBuilder]::new()
        [void]$sb.AppendLine('<Project Sdk="Microsoft.NET.Sdk">')
        [void]$sb.AppendLine('  <PropertyGroup>')
        [void]$sb.AppendLine('    <TargetFramework>net10.0-windows</TargetFramework>')
        [void]$sb.AppendLine('    <AssemblyName>' + $name + '</AssemblyName>')
        [void]$sb.AppendLine('    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>')
        [void]$sb.AppendLine('    <Nullable>disable</Nullable>')
        [void]$sb.AppendLine('    <LangVersion>latest</LangVersion>')
        [void]$sb.AppendLine('    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>')
        [void]$sb.AppendLine('    <NoWarn>$(NoWarn);CS0436;MSB3246;MSB3277</NoWarn>')
        [void]$sb.AppendLine('  </PropertyGroup>')
        [void]$sb.AppendLine('  <ItemGroup>')
        [void]$sb.AppendLine('    <Compile Include="' + $filePath + '" />')
        [void]$sb.AppendLine('  </ItemGroup>')
        [void]$sb.AppendLine('  <ItemGroup>')
        foreach ($reference in $references)
        {
            $base = [System.IO.Path]::GetFileNameWithoutExtension($reference)
            [void]$sb.AppendLine('    <Reference Include="' + $base + '"><HintPath>' + $reference + '</HintPath><Private>false</Private></Reference>')
        }
        [void]$sb.AppendLine('  </ItemGroup>')
        [void]$sb.AppendLine('</Project>')
        Set-Content -Path (Join-Path $buildDir "$name.csproj") -Value $sb.ToString() -Encoding UTF8

        Write-Host '  编译中…'
        $buildLog = & dotnet build (Join-Path $buildDir "$name.csproj") -c Release -v q 2>&1
        if ($LASTEXITCODE -ne 0)
        {
            $problems.Add("$name 编译失败")
            $buildLog | Select-String -Pattern 'error' | Select-Object -First 5 | ForEach-Object { Write-Host "    $($_.Line)" }
            continue
        }

        $builtDll = Join-Path $buildDir "bin\Release\net10.0-windows\$name.dll"
        $env:OMNI_HOST_DIR   = $hostPath
        $env:DALAMUD_DEV_DIR = $DalamudDevDir
        Write-Host '  契约预检：'
        $checkLog = & dotnet $ModuleCheckDll $builtDll 2>&1
        $checkLog | ForEach-Object { Write-Host "    $_" }
        if ($LASTEXITCODE -ne 0) { $problems.Add("$name 未通过契约预检") }
    }
}

# ---------- 写回 SHA256 ----------

if ($Update)
{
    foreach ($module in $manifest.Modules)
    {
        $filePath = Join-Path $repoRoot $module.File
        if (-not (Test-Path $filePath)) { continue }

        $hash = (Get-FileHash $filePath -Algorithm SHA256).Hash

        $anchor = '"InternalName": "' + $module.InternalName + '"'
        $start  = $manifestText.IndexOf($anchor, [StringComparison]::Ordinal)
        if ($start -lt 0)
        {
            Write-Host "  找不到 $($module.InternalName) 的 InternalName 行，跳过" -ForegroundColor Yellow
            continue
        }

        $hashIndex = $manifestText.IndexOf('"Sha256"', $start, [StringComparison]::Ordinal)
        if ($hashIndex -lt 0) { continue }

        $openQuote = $manifestText.IndexOf('"', $manifestText.IndexOf(':', $hashIndex) + 1)
        $closeQuote = $manifestText.IndexOf('"', $openQuote + 1)

        $manifestText = $manifestText.Substring(0, $openQuote + 1) + $hash + $manifestText.Substring($closeQuote)
        Write-Host "已写回 $($module.InternalName) 的 SHA256"
    }

    [System.IO.File]::WriteAllText($manifestPath, $manifestText, $utf8NoBom)
}

# ---------- 结论 ----------

if ($problems.Count -eq 0)
{
    Write-Host "`n全部通过，可以推 GitHub 了。" -ForegroundColor Green
    exit 0
}

Write-Host "`n还有 $($problems.Count) 个问题：" -ForegroundColor Yellow
$problems | ForEach-Object { Write-Host "  - $_" }
exit 1
