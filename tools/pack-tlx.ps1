# 打 .tlx 分发包：构建插件并把输出目录【内容】压成 zip（改名 .tlx）。
# .tlx = zip，根目录直接含 manifest.json + dll + runtimes/ 子树（勿套外层文件夹）。
# ONNX 原生库在 runtimes/win-x64/native/，靠整树递归打入、勿扁平化。
# 只有 .tlx 文件能被 TuneLab 安装（拖文件夹不装，见 Editor.OnDrop / InstallExtensions）。
# 用法: pwsh tools/pack-tlx.ps1 [-Configuration Release]
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo = Split-Path $PSScriptRoot -Parent
$source = Join-Path $repo "bin/$Configuration/net8.0"
$out = Join-Path $PSScriptRoot "tlx"

dotnet build (Join-Path $repo "DiffSingerForTuneLab.csproj") -c $Configuration
dotnet build (Join-Path $repo "MLRuntime/MLRuntime.csproj") -c $Configuration

# MLRuntime.exe 子进程：暂存进插件输出的 mlruntime/ 子目录（自带 onnxruntime + runtimes/），随后一并打进 .tlx。
$mlSource = Join-Path $repo "MLRuntime/bin/$Configuration/net8.0"
$mlStage = Join-Path $source "mlruntime"
if (Test-Path $mlStage) { Remove-Item $mlStage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $mlStage | Out-Null
Copy-Item -Path (Join-Path $mlSource "*") -Destination $mlStage -Recurse -Force

# 去重 onnxruntime：MLRuntime 子进程经 OnnxNativeResolver 改从父插件目录 runtimes/ 加载原生库，
# 不再自带一份（省 ~15MB×平台）。删掉暂存里的 mlruntime/runtimes/（父目录整树 runtimes/ 仍在）。
$mlRuntimes = Join-Path $mlStage "runtimes"
if (Test-Path $mlRuntimes) { Remove-Item $mlRuntimes -Recurse -Force }

# 剪除输出【根目录】冗余的原生库副本 + DirectML 调试符号：规范副本在 runtimes/<rid>/native/，
# 宿主经 deps.json（AssemblyDependencyResolver）从那里解析、根部这几份用不上。某些 SDK 版本
# （如 CI 的 10.x）会把它们额外拷到输出根、白占 ~17MB，某些（本机 8.x）不拷——显式剪除以保证
# 跨环境产物一致、精简。只删根部，runtimes/ 整树不动。
foreach ($f in 'DirectML.dll','DirectML.pdb','DirectML.Debug.dll','DirectML.Debug.pdb','onnxruntime.dll','onnxruntime.lib','onnxruntime_providers_shared.dll','onnxruntime_providers_shared.lib') {
    $p = Join-Path $source $f
    if (Test-Path $p) { Remove-Item $p -Force }
}

# 只保留 win-x64 的原生库：manifest 声明 platforms=["win-x64"]，别的 RID 树在任何情形下都不会被加载
# ——宿主自身只发 win-x64 / osx-arm64 / linux-x64（无 win-arm64），而后两者上本插件整个被 platforms 过滤掉。
# onnxruntime 的 nuget 却按多 RID 打包（win-arm64 那份 ~17MB，占包四成），故在此剪除。
# 宿主经 deps.json 按当前 RID 解析原生库，剪掉用不到的 RID 不影响 win-x64 的解析。
# 将来宿主支持 win-arm64 时：把这里放开 + csproj 补拷 arm64 的 DirectML.dll + MLRuntime 按 RID publish
# （apphost 是 per-RID 的），且必须在真机实测后再发——不是删掉这段就能支持。
$keepRid = 'win-x64'
$runtimesDir = Join-Path $source "runtimes"
if (Test-Path $runtimesDir) {
    foreach ($rid in Get-ChildItem $runtimesDir -Directory) {
        if ($rid.Name -eq $keepRid) { continue }
        $size = (Get-ChildItem $rid.FullName -Recurse -File | Measure-Object -Property Length -Sum).Sum
        Remove-Item $rid.FullName -Recurse -Force
        Write-Host ("已剪除 runtimes/{0}（{1:N1} MB，非 {2}）" -f $rid.Name, ($size / 1MB), $keepRid)
    }
}

# 从 manifest.json 取 id + version 命名产物
$desc = Get-Content (Join-Path $source "manifest.json") -Raw | ConvertFrom-Json
$tlx = Join-Path $out ("$($desc.id)-$($desc.version).tlx")

New-Item -ItemType Directory -Force -Path $out | Out-Null
if (Test-Path $tlx) { Remove-Item $tlx -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($source, $tlx)

Write-Host "已打包 $tlx"
