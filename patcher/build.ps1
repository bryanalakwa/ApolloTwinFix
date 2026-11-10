# Builds patcher\ApolloTwinFix.exe: React UI (single HTML) + WebView2 DLLs embedded
# in one .NET Framework 4.8 exe, compiled with the csc.exe that ships with Windows.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$obj = Join-Path $root 'app\obj'
New-Item -ItemType Directory -Force $obj | Out-Null

# 1. UI -> ui\dist\index.html
Push-Location (Join-Path $root 'ui')
if (-not (Test-Path node_modules)) { npm ci --no-fund --no-audit }
npx vite build
if ($LASTEXITCODE) { throw 'UI build failed' }
Pop-Location

# 2. WebView2 SDK (pinned), Microsoft-signed DLLs only
$ver = '1.0.4258.31'
$pkg = Join-Path $obj "webview2.$ver"
if (-not (Test-Path $pkg)) {
    $zip = "$pkg.zip"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$ver/microsoft.web.webview2.$ver.nupkg" -OutFile $zip
    Expand-Archive $zip $pkg
    Remove-Item $zip
}
$core = "$pkg\lib\net462\Microsoft.Web.WebView2.Core.dll"
$winforms = "$pkg\lib\net462\Microsoft.Web.WebView2.WinForms.dll"
$loader = "$pkg\runtimes\win-x64\native\WebView2Loader.dll"
foreach ($f in $core, $winforms, $loader) {
    $sig = Get-AuthenticodeSignature $f
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw "Bad signature: $f" }
}

# 3. Compile
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$out = Join-Path $root 'ApolloTwinFix.exe'
& "$fw\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /out:$out `
    /win32manifest:"$root\app\app.manifest" /win32icon:"$root\app\icon.ico" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.Web.Extensions.dll `
    /r:$core /r:$winforms `
    /resource:"$root\ui\dist\index.html,index.html" `
    /resource:"$core,Microsoft.Web.WebView2.Core.dll" `
    /resource:"$winforms,Microsoft.Web.WebView2.WinForms.dll" `
    /resource:"$loader,WebView2Loader.dll" `
    "$root\app\Program.cs"
if ($LASTEXITCODE) { throw 'Compile failed' }
"Built $out ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"
