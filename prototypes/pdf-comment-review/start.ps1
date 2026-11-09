param([switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$taskUrl = 'http://127.0.0.1:4179/'
$taskHealth = $taskUrl + 'api/health'
function Test-PrototypeServer {
    try {
        $response = Invoke-RestMethod -Uri $taskHealth -TimeoutSec 1
        return $response.ok -eq $true -and $response.service -eq 'texeed-pdf-comment-review'
    } catch { return $false }
}
try {
    if (-not (Test-PrototypeServer)) {
        $taskNode = Get-Command node.exe -ErrorAction Stop
        if (-not (Test-Path -LiteralPath 'C:\texlive\2026\bin\windows\xelatex.exe')) { throw '找不到 XeLaTeX：C:\texlive\2026\bin\windows\xelatex.exe' }
        if (-not (Test-Path -LiteralPath 'C:\texlive\2026\bin\windows\synctex.exe')) { throw '找不到 SyncTeX：C:\texlive\2026\bin\windows\synctex.exe' }
        $taskPoppler = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.cache/codex-runtimes/codex-primary-runtime/dependencies/native/poppler/Library/bin'
        if (-not (Test-Path -LiteralPath (Join-Path $taskPoppler 'pdftoppm.exe')) -or -not (Test-Path -LiteralPath (Join-Path $taskPoppler 'pdfinfo.exe'))) { throw '找不到 PDF 页面渲染工具 pdftoppm 或 pdfinfo。' }
        $taskLog = Join-Path ([IO.Path]::GetTempPath()) 'texeed-pdf-review-server.log'
        $taskError = Join-Path ([IO.Path]::GetTempPath()) 'texeed-pdf-review-server-error.log'
        $env:TEXEED_PDF_PROTOTYPE_PORT = '4179'
        $taskProcess = Start-Process -FilePath $taskNode.Source -ArgumentList @('"' + (Join-Path $PSScriptRoot 'server.cjs') + '"') -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -RedirectStandardOutput $taskLog -RedirectStandardError $taskError -PassThru
        for ($taskAttempt = 0; $taskAttempt -lt 30; $taskAttempt++) {
            if (Test-PrototypeServer) { break }
            if ($taskProcess.HasExited) { throw "服务启动失败（端口 4179 可能被占用）：$(Get-Content -LiteralPath $taskError -Raw -ErrorAction SilentlyContinue)" }
            Start-Sleep -Milliseconds 200
            $taskProcess.Refresh()
        }
        if (-not (Test-PrototypeServer)) { throw '服务启动超时，请检查端口 4179 和 Node.js。' }
    }
    if (-not $NoBrowser) { Start-Process $taskUrl }
} catch {
    [Console]::Error.WriteLine("无法启动 PDF 评论原型：$($_.Exception.Message)")
    exit 1
}
