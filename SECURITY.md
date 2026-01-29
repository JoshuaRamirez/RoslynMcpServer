# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in Roslyn MCP Server, please report it responsibly:

1. **Do NOT** open a public GitHub issue for security vulnerabilities
2. Instead, use GitHub's private security advisory feature:
   - Go to https://github.com/JoshuaRamirez/RoslynMcpServer/security/advisories
   - Click "Report a vulnerability"
3. Or email the maintainers directly (check repository for contact information)

We will respond to security reports within 48 hours and work with you to address the issue promptly.

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 0.1.x   | :white_check_mark: |

## Security Considerations

Roslyn MCP Server executes code refactoring operations on your codebase. To use it safely:

### For Users

- **Always review changes in preview mode** before applying them to your codebase
- **Run the server in a trusted environment** - it has access to your source code
- **Ensure your solution files are backed up** before performing refactoring operations
- **Use version control** (Git) to track and revert changes if needed
- **Validate MCP client connections** - only connect from trusted AI assistants

### For Developers

- **Input validation**: All refactoring parameters are validated before execution
- **Atomic operations**: File writes use atomic operations with rollback on failure
- **No network access**: The server does not make outbound network connections
- **Read-only by default**: Preview mode allows inspection without modification
- **MSBuild isolation**: Workspace operations are isolated per request

### Known Security Limitations

1. **File system access**: The server requires read/write access to your solution directory
2. **MSBuild execution**: Loading solutions executes MSBuild, which can run custom build tasks
3. **No authentication**: The MCP protocol does not include built-in authentication (rely on process isolation)

## Security Best Practices

When deploying Roslyn MCP Server:

1. **Limit file system access** to only the directories containing your solutions
2. **Run with least privilege** - do not run as administrator/root
3. **Use in development environments only** - not recommended for production servers
4. **Monitor refactoring operations** - review logs for unexpected behavior
5. **Keep dependencies updated** - regularly update Roslyn and .NET SDK versions

## Disclosure Policy

When we receive a security report:

1. We will confirm receipt within 48 hours
2. We will investigate and provide an initial assessment within 5 business days
3. We will work with the reporter to understand and reproduce the issue
4. We will develop and test a fix
5. We will coordinate disclosure timing with the reporter
6. We will credit the reporter (unless they prefer to remain anonymous)

## Security Updates

Security updates will be released as:

- **Patch versions** (0.1.x) for minor security fixes
- **Minor versions** (0.x.0) for significant security improvements
- **GitHub Security Advisories** for all security-related releases

Subscribe to repository releases and security advisories to stay informed.

## Contact

For security-related questions or concerns, please use the private security advisory feature on GitHub or contact the repository maintainers.

Thank you for helping keep Roslyn MCP Server and its users safe!

