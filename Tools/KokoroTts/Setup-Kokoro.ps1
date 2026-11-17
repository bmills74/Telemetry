param([string]$Python)
$ErrorActionPreference = 'Stop'
$kokoroHome = $env:SIMCOACH_KOKORO_HOME
if (-not $kokoroHome) { $kokoroHome = Join-Path $env:LOCALAPPDATA 'SimCoach/Kokoro' }
if (-not $Python) {
    $Python = Get-ChildItem "$env:LOCALAPPDATA/Programs/Python/Python3*/python.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Python) { throw 'Install Python 3.10–3.13, then pass its executable with -Python.' }
New-Item -ItemType Directory -Force -Path $kokoroHome | Out-Null
& $Python -m venv (Join-Path $kokoroHome '.venv')
if ($LASTEXITCODE -ne 0) { throw 'Could not create the Kokoro Python environment.' }
$kokoroPython = Join-Path $kokoroHome '.venv/Scripts/python.exe'
& $kokoroPython -m pip install 'kokoro-onnx==0.6.1' 'soundfile==0.13.1'
if ($LASTEXITCODE -ne 0) { throw 'Kokoro dependency installation failed.' }
foreach ($name in @('kokoro-v1.0.onnx', 'voices-v1.0.bin')) {
    $destination = Join-Path $kokoroHome $name
    if (-not (Test-Path -LiteralPath $destination)) {
        Invoke-WebRequest "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.1/$name" -OutFile "$destination.download"
        Move-Item -LiteralPath "$destination.download" -Destination $destination
    }
}
& $kokoroPython -m pip install 'pyinstaller==6.19.0'
if ($LASTEXITCODE -ne 0) { throw 'Could not install the worker packager.' }
& $kokoroPython -m PyInstaller --noconfirm --clean --onedir --name KokoroWorker --collect-all kokoro_onnx --collect-all espeakng_loader --collect-all phonemizer --collect-all onnxruntime --collect-all language_tags --collect-all segments --collect-all soundfile --collect-all _soundfile_data --distpath (Join-Path $PSScriptRoot 'runtime') --workpath (Join-Path $PSScriptRoot 'build') --specpath (Join-Path $PSScriptRoot 'build') (Join-Path $PSScriptRoot 'kokoro_tts.py')
if ($LASTEXITCODE -ne 0) { throw 'Could not package the Kokoro worker.' }
$bundle = Join-Path $PSScriptRoot 'runtime/KokoroWorker'
Copy-Item -LiteralPath (Join-Path $kokoroHome 'kokoro-v1.0.onnx'), (Join-Path $kokoroHome 'voices-v1.0.bin') -Destination $bundle
Write-Output "Bundled Kokoro worker and models in $bundle. Build or publish SimCoach to include them."
