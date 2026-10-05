$ErrorActionPreference = 'Stop'
$paperRoot = $PSScriptRoot
$paperWord = New-Object -ComObject Word.Application
$paperDoc = $null
try {
    $paperWord.Visible = $false
    $paperWord.DisplayAlerts = 0
    $paperWord.AutomationSecurity = 3
    $paperDoc = $paperWord.Documents.Open((Join-Path $paperRoot 'output\docx\tokensaver-research-editable.docx'), $false, $true)
    $paperDoc.ExportAsFixedFormat((Join-Path $paperRoot 'tmp\docx-qa\word-render.pdf'), 17)
    $paperReport = [PSCustomObject]@{
        Pages = $paperDoc.ComputeStatistics(2)
        NativeEquations = $paperDoc.OMaths.Count
        WordVersion = $paperWord.Version
        RenderEngine = 'Microsoft Word'
        CanonicalRendererLimitation = 'No bundled LibreOffice executable on this Windows host'
    }
    $paperReport | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $paperRoot 'tmp\docx-qa\word-render.json')
    $paperReport | ConvertTo-Json
} finally {
    if ($null -ne $paperDoc) { $paperDoc.Close(0) }
    $paperWord.Quit()
}
