param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '需要 .NET Framework 4.8 开发编译器。' }
$releaseDirectory = Join-Path $projectDirectory 'release'
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$executablePath = Join-Path $releaseDirectory '奶蛙桌宠.exe'
$arguments = @('/nologo','/utf8output','/codepage:65001','/target:winexe','/platform:anycpu','/optimize+','/debug-', '/errorreport:none',
    '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
    '/r:System.Runtime.Serialization.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',
    ('/out:' + $executablePath),
    ('/win32icon:' + (Join-Path $projectDirectory 'assets/pet.ico')),
    ('/resource:' + (Join-Path $projectDirectory 'assets/animations.zip') + ',Naifrog.Animations'),
    ('/resource:' + (Join-Path $projectDirectory 'assets/pet.ico') + ',Naifrog.Icon'),
    (Join-Path $projectDirectory 'src/Animation.cs'), (Join-Path $projectDirectory 'src/PetWindow.cs'), (Join-Path $projectDirectory 'src/Program.cs'))
& $compilerPath @arguments
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
if ($Test) {
    $testDirectory = Join-Path $projectDirectory 'test-artifacts'
    New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
    $reportPath = Join-Path $testDirectory 'self-test.json'
    $process = Start-Process -FilePath $executablePath -ArgumentList @('--self-test', ('"' + $reportPath + '"')) -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw '自动检查失败。' }
    Get-Content -LiteralPath $reportPath | ConvertFrom-Json | Select-Object passed,failed
}
Write-Output '构建完成：release/奶蛙桌宠.exe'
