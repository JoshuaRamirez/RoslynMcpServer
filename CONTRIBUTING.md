# Contributing to Roslyn MCP Server

Thank you for your interest in contributing to Roslyn MCP Server! This document provides guidelines and instructions for contributing.

## 🎯 Code of Conduct

Be respectful, inclusive, and professional in all interactions.

## 🚀 Getting Started

### Prerequisites

- .NET 9.0 SDK or later
- Git
- A code editor (Visual Studio, VS Code, or Rider recommended)

### Setting Up Your Development Environment

1. Fork the repository on GitHub
2. Clone your fork locally:
   ```bash
   git clone https://github.com/JoshuaRamirez/RoslynMcpServer.git
   cd RoslynMcpServer
   ```
3. Add the upstream repository:
   ```bash
   git remote add upstream https://github.com/JoshuaRamirez/RoslynMcpServer.git
   ```
4. Restore dependencies:
   ```bash
   dotnet restore
   ```
5. Build the solution:
   ```bash
   dotnet build
   ```
6. Run tests to verify everything works:
   ```bash
   dotnet test
   ```

## 📝 Making Changes

### Branch Naming Convention

- `feature/description` - New features
- `fix/description` - Bug fixes
- `docs/description` - Documentation updates
- `refactor/description` - Code refactoring
- `test/description` - Test additions or updates

### Commit Message Guidelines

Follow the [Conventional Commits](https://www.conventionalcommits.org/) specification:

```
<type>(<scope>): <subject>

<body>

<footer>
```

**Types:**
- `feat`: New feature
- `fix`: Bug fix
- `docs`: Documentation changes
- `style`: Code style changes (formatting, no logic change)
- `refactor`: Code refactoring
- `test`: Adding or updating tests
- `chore`: Maintenance tasks

**Examples:**
```
feat(core): add support for extract interface refactoring

fix(server): resolve null reference in tool registry

docs(readme): update installation instructions

test(core): add tests for rename symbol operation
```

## 🧪 Testing Requirements

### Writing Tests

- All new features must include unit tests
- Bug fixes should include regression tests
- Aim for high code coverage (>80%)
- Use descriptive test names that explain what is being tested

### Test Structure

```csharp
[Fact]
public void MethodName_Scenario_ExpectedBehavior()
{
    // Arrange
    var input = ...;
    
    // Act
    var result = ...;
    
    // Assert
    Assert.Equal(expected, result);
}
```

### Running Tests

```bash
# Run all tests
dotnet test

# Run tests with coverage
dotnet test /p:CollectCoverage=true

# Run specific test project
dotnet test tests/RoslynMcp.Core.Tests
```

## 📐 Code Style Guidelines

### C# Coding Standards

- Follow [Microsoft C# Coding Conventions](https://docs.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- Use meaningful variable and method names
- Keep methods focused and small (ideally <50 lines)
- Add XML documentation comments for all public APIs
- Enable nullable reference types and handle nullability properly

### File Organization

- One type per file (class, interface, struct, enum)
- File name should match the type name
- Organize using statements alphabetically
- Use `namespace` file-scoped declarations (C# 10+)

### Example:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynMcp.Core.Refactoring;

/// <summary>
/// Performs rename symbol refactoring operations.
/// </summary>
public sealed class RenameSymbolOperation
{
    // Implementation
}
```

## 🔍 Pull Request Process

1. **Update your fork** with the latest upstream changes:
   ```bash
   git fetch upstream
   git rebase upstream/main
   ```

2. **Create a feature branch**:
   ```bash
   git checkout -b feature/my-new-feature
   ```

3. **Make your changes** following the guidelines above

4. **Run tests** and ensure they all pass:
   ```bash
   dotnet test
   ```

5. **Build in Release mode** to check for warnings:
   ```bash
   dotnet build -c Release
   ```

6. **Commit your changes** with clear commit messages

7. **Push to your fork**:
   ```bash
   git push origin feature/my-new-feature
   ```

8. **Open a Pull Request** on GitHub with:
   - Clear title describing the change
   - Description of what changed and why
   - Reference to any related issues
   - Screenshots/examples if applicable

### PR Review Checklist

Before submitting, ensure:

- [ ] Code builds without errors or warnings
- [ ] All tests pass
- [ ] New code has appropriate test coverage
- [ ] XML documentation added for public APIs
- [ ] No sensitive data (credentials, API keys) in code
- [ ] Code follows project style guidelines
- [ ] Commit messages follow conventional commits format
- [ ] PR description clearly explains the changes

## 🐛 Reporting Bugs

Use the [Bug Report template](.github/ISSUE_TEMPLATE/bug_report.md) and include:

- Clear description of the issue
- Steps to reproduce
- Expected vs actual behavior
- Environment details (OS, .NET version)
- Relevant logs or error messages

## 💡 Suggesting Features

Use the [Feature Request template](.github/ISSUE_TEMPLATE/feature_request.md) and include:

- Clear description of the proposed feature
- Use cases and benefits
- Potential implementation approach (if applicable)

## 📚 Documentation

Documentation improvements are always welcome:

- Fix typos or clarify existing docs
- Add examples and usage scenarios
- Improve API documentation
- Update design documents

## ❓ Questions

For questions and discussions:

- Check existing [GitHub Discussions](https://github.com/JoshuaRamirez/RoslynMcpServer/discussions)
- Review the [Design documentation](./Design)
- Open a new discussion if your question isn't answered

## 🏆 Recognition

Contributors will be recognized in:

- GitHub contributors list
- Release notes for significant contributions
- Project documentation

Thank you for contributing to Roslyn MCP Server!


