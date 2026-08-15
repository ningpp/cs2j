$target = 'D:\cs-java-81319-8'
$resolved = (Resolve-Path -LiteralPath $target -ErrorAction SilentlyContinue).Path
if ($resolved -and $resolved.StartsWith('D:\cs-java-81319-8')) {
    Remove-Item -LiteralPath $resolved -Recurse -Force
    Write-Output "removed $resolved"
} else {
    Write-Output 'nothing to remove'
}
