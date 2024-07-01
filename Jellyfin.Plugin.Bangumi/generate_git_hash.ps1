# generate_git_hash.ps1
$gitHash = git rev-parse --short HEAD
$output = @"
// This file is auto-generated.
namespace MyNamespace
{
    public static class GitInfo
    {
        public const string Hash = `"$gitHash`";
    }
}
"@
Write-Host "Generated GitInfo with hash $gitHash" -ForegroundColor Yellow
$output | Out-File -Encoding utf8 -NoNewline "GitInfo.cs"