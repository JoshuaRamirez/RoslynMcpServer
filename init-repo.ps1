# RoslynMcp Repository Initialization Script
# This script helps you initialize the Git repository and push to GitHub

param(
    [Parameter(Mandatory=$true)]
    [string]$GitHubUsername,
    
    [Parameter(Mandatory=$false)]
    [string]$RepositoryName = "RoslynMcpServer"
)

Write-Host "🚀 Roslyn MCP Server - Repository Initialization" -ForegroundColor Cyan
Write-Host "=================================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Update repository URLs in project files
Write-Host "📝 Step 1: Updating repository URLs..." -ForegroundColor Yellow

$repoUrl = "https://github.com/$GitHubUsername/$RepositoryName"

$filesToUpdate = @(
    "src/RoslynMcp.Contracts/RoslynMcp.Contracts.csproj",
    "src/RoslynMcp.Core/RoslynMcp.Core.csproj",
    "README.md",
    "CONTRIBUTING.md"
)

foreach ($file in $filesToUpdate) {
    if (Test-Path $file) {
        $content = Get-Content $file -Raw
        $content = $content -replace "YOUR_USERNAME", $GitHubUsername
        $content = $content -replace "ORIGINAL_OWNER", $GitHubUsername
        Set-Content $file $content -NoNewline
        Write-Host "  ✓ Updated $file" -ForegroundColor Green
    }
}

Write-Host ""

# Step 2: Initialize Git repository
Write-Host "📦 Step 2: Initializing Git repository..." -ForegroundColor Yellow

if (Test-Path ".git") {
    Write-Host "  ⚠ Git repository already initialized" -ForegroundColor Yellow
} else {
    git init
    Write-Host "  ✓ Git repository initialized" -ForegroundColor Green
}

Write-Host ""

# Step 3: Add all files
Write-Host "📁 Step 3: Staging files..." -ForegroundColor Yellow
git add .
Write-Host "  ✓ All files staged" -ForegroundColor Green
Write-Host ""

# Step 4: Create initial commit
Write-Host "💾 Step 4: Creating initial commit..." -ForegroundColor Yellow
git commit -m "Initial commit: Roslyn MCP Server v0.1.0

- 8 refactoring operations (move_type_to_file, move_type_to_namespace, rename_symbol, extract_method, etc.)
- Comprehensive test coverage
- GitHub Actions CI/CD pipelines
- NuGet package support
- MIT License"

Write-Host "  ✓ Initial commit created" -ForegroundColor Green
Write-Host ""

# Step 5: Create GitHub repository (if gh CLI is available)
Write-Host "🌐 Step 5: Creating GitHub repository..." -ForegroundColor Yellow

if (Get-Command gh -ErrorAction SilentlyContinue) {
    $createRepo = Read-Host "Create GitHub repository now? (y/n)"
    
    if ($createRepo -eq 'y') {
        gh repo create $RepositoryName --public --source=. --remote=origin --description "Model Context Protocol server for Roslyn-powered C# refactoring operations"
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✓ GitHub repository created" -ForegroundColor Green
            
            # Push to GitHub
            Write-Host ""
            Write-Host "📤 Step 6: Pushing to GitHub..." -ForegroundColor Yellow
            git push -u origin main
            Write-Host "  ✓ Code pushed to GitHub" -ForegroundColor Green
        } else {
            Write-Host "  ✗ Failed to create repository" -ForegroundColor Red
        }
    } else {
        Write-Host "  ⏭ Skipped - Create repository manually" -ForegroundColor Yellow
    }
} else {
    Write-Host "  ⚠ GitHub CLI (gh) not found" -ForegroundColor Yellow
    Write-Host "  📝 Create repository manually at: https://github.com/new" -ForegroundColor Cyan
    Write-Host "  Then run: git remote add origin $repoUrl" -ForegroundColor Cyan
    Write-Host "           git push -u origin main" -ForegroundColor Cyan
}

Write-Host ""
Write-Host "✅ Repository initialization complete!" -ForegroundColor Green
Write-Host ""
Write-Host "📋 Next steps:" -ForegroundColor Cyan
Write-Host "  1. Configure branch protection for 'main' branch" -ForegroundColor White
Write-Host "  2. Add NUGET_API_KEY secret for package publishing" -ForegroundColor White
Write-Host "  3. Enable GitHub Discussions (recommended)" -ForegroundColor White
Write-Host "  4. Add repository topics: roslyn, mcp, csharp, refactoring" -ForegroundColor White
Write-Host "  5. Create initial release (v0.1.0)" -ForegroundColor White
Write-Host ""
Write-Host "📚 See RELEASE_CHECKLIST.md for detailed instructions" -ForegroundColor Cyan

