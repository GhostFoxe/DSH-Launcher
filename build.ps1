# build.ps1 - 一键编译 DSH-Launcher
#
# 顺序：
#   [1/2] 用 DSH-uninstall.cs 编出 uninstall.exe
#   [2/2] 用 DSH-Launcher.cs 编出 DSH-Launcher.exe，并把 uninstall.exe + 图标 + WebView2 DLL
#         作为内嵌资源打进主程序
#
# 用法（二选一）：
#   双击 build.cmd
#   或命令行：  powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
#
# 前置条件：64 位 Windows + .NET Framework 4.x（csc.exe）。

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$stage = Join-Path $root '.build'
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# 定位 .NET Framework 4.x 的 csc.exe（64 位优先）
$candidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw '未找到 .NET Framework 4.x 的 csc.exe，请先安装 .NET Framework 4.x。' }

function Invoke-Csc([string[]]$Params) {
    Write-Host ('  csc ' + ($Params -join ' '))
    & $csc @Params
    if ($LASTEXITCODE -ne 0) { throw ('csc 编译失败，退出码 ' + $LASTEXITCODE) }
}

# csc /resource 的标识符默认为“文件基名”，所以直接传绝对路径即可，
# 内嵌资源名会变成 xiezai.ico / uninstall.exe / sources.json 等（与代码里的
# GetManifestResourceStream 名称一致）。
Write-Host '[1/2] 编译卸载器 uninstall.exe ...'
Invoke-Csc @(
    '/nologo', '/target:winexe',
    ('/out:' + (Join-Path $stage 'uninstall.exe')),
    '/r:System.Management.dll',
    (Join-Path $root 'DSH-uninstall.cs'),
    ('/resource:' + (Join-Path $assets 'xiezai.ico')),
    ('/resource:' + (Join-Path $assets 'xiezai.png')),
    ('/win32icon:' + (Join-Path $assets 'xiezai.ico'))
)

Write-Host '[2/2] 编译主程序 DSH-Launcher.exe ...'
Invoke-Csc @(
    '/nologo', '/target:winexe',
    ('/out:' + (Join-Path $stage 'DSH-Launcher.exe')),
    ('/r:' + (Join-Path $assets 'Microsoft.Web.WebView2.Core.dll')),
    ('/r:' + (Join-Path $assets 'Microsoft.Web.WebView2.WinForms.dll')),
    '/r:System.Net.Http.dll',
    '/r:System.IO.Compression.dll',
    '/r:System.IO.Compression.FileSystem.dll',
    (Join-Path $root 'DSH-Launcher.cs'),
    ('/resource:' + (Join-Path $assets 'Microsoft.Web.WebView2.Core.dll')),
    ('/resource:' + (Join-Path $assets 'Microsoft.Web.WebView2.WinForms.dll')),
    ('/resource:' + (Join-Path $assets 'WebView2Loader.dll')),
    ('/resource:' + (Join-Path $stage 'uninstall.exe')),
    ('/resource:' + (Join-Path $root 'sources.json')),
    ('/resource:' + (Join-Path $assets 'dafeiyu.png')),
    ('/resource:' + (Join-Path $assets 'error.png')),
    ('/resource:' + (Join-Path $assets 'dafeiyu-glow.png')),
    ('/resource:' + (Join-Path $assets 'dafeiyu.ico')),
    ('/win32icon:' + (Join-Path $assets 'dafeiyu.ico'))
)

# 产物复制到仓库根目录
$final = Join-Path $root 'DSH-Launcher.exe'
Copy-Item (Join-Path $stage 'DSH-Launcher.exe') $final -Force

Write-Host ''
Write-Host ('构建完成: ' + $final)
Write-Host ('SHA256:   ' + (Get-FileHash $final -Algorithm SHA256).Hash)
