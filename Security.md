# Security Policy

This document outlines the security policy for Dotty, including supported versions and how to report security vulnerabilities.

## Supported Versions

The following table indicates which versions of Dotty are currently supported with security updates:

| Version | Supported          | Status      |
| ------- | ------------------ | ----------- |
| 0.3.x   | :white_check_mark: | Current     |
| 0.2.x   | :x:                | End of life |
| 0.1.x   | :x:                | End of life |
| < 0.1   | :x:                | End of life |

### Version Support Policy

- **Current version** (latest minor/patch): Receives all security updates
- **Previous versions**: Security support ends when a new minor version is released
- **Pre-release versions** (alpha, beta, rc): No security support guarantees

### Platform Support

Security updates are provided for all supported platforms:

| Platform | Minimum Version | Notes |
|----------|-----------------|-------|
| Linux    | Kernel 3.10+    | glibc 2.17+ required |
| macOS    | 10.15+          | Catalina or later |
| Windows  | 10 1809+        | Windows 10 or Windows 11 |

## Reporting a Vulnerability

### Private Disclosure Process

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, please report security vulnerabilities privately using one of the following methods:

#### Preferred Method: GitHub Security Advisories

1. Navigate to the repository's [Security Advisories](https://github.com/dominic-codespoti/dotty/security/advisories) page
2. Click "New draft security advisory"
3. Provide detailed information about the vulnerability

#### Alternative Method: Email

Send an email to the maintainers at:
- **security@dotty-terminal.dev** (if configured)
- Or contact the repository owner through GitHub

### What to Include

When reporting a vulnerability, please include:

1. **Description**: Clear description of the vulnerability
2. **Impact**: What could an attacker achieve?
3. **Reproduction**: Step-by-step instructions to reproduce
4. **Affected versions**: Which versions are impacted?
5. **Environment**: OS, .NET version, Dotty version
6. **Proof of concept**: If applicable, provide minimal code demonstrating the issue
7. **Suggested fix**: If you have one, describe a potential remediation

### Response Timeline

| Phase | Target Timeline | Action |
|-------|-----------------|--------|
| Acknowledgment | Within 48 hours | Confirm receipt of report |
| Initial Assessment | Within 5 days | Determine severity and impact |
| Fix Development | Severity-dependent | Develop and test fix |
| Disclosure | Coordinated | Public disclosure with fix release |

### Severity Classification

We follow the [CVSS v3.1](https://www.first.org/cvss/v3.1/specification_document) standard for severity classification:

| Severity | Score | Response Time |
|----------|-------|---------------|
| Critical | 9.0 - 10.0 | 7 days |
| High | 7.0 - 8.9 | 14 days |
| Medium | 4.0 - 6.9 | 30 days |
| Low | 0.1 - 3.9 | Next release |

## Security Considerations

### Known Security Boundaries

Dotty operates with the following security considerations:

1. **Process Isolation**: Terminal processes are spawned in separate PTY sessions
2. **Input Handling**: All input sequences are validated before processing
3. **Memory Safety**: Uses managed .NET code with unsafe blocks documented and minimized
4. **Native Interop**: C pty-helper runs with minimal privileges

### Security Features

- Input sanitization for control sequences
- Bounds checking on all buffer operations
- Safe handling of escape sequences to prevent injection attacks

### Areas of Concern

As a terminal emulator, Dotty handles:

- **Arbitrary input from shell processes**: Malicious applications could attempt escape sequence injection
- **Clipboard integration**: Paste operations could potentially execute commands (common to all terminals)
- **Hyperlinks (OSC 8)**: URLs in terminal content could be malicious

### Best Practices for Users

1. Only run trusted shell commands and applications
2. Be cautious when clicking hyperlinks displayed in the terminal
3. Review clipboard contents before pasting commands
4. Keep Dotty updated to the latest version

## Security Best Practices for Development

When contributing to Dotty, please follow these security guidelines:

### Code Review

- All code involving native interop requires security review
- Input parsing code must validate all inputs
- Unsafe code blocks must include security comments

### Dependencies

- Keep all dependencies up to date
- Review security advisories for Avalonia, .NET runtime, and other dependencies
- Pin dependency versions in production releases

### Testing

- Include security-focused test cases
- Test boundary conditions and malformed input
- Use fuzzing for input parsing components

## Disclosure Policy

We follow a **coordinated disclosure** policy:

1. Reporter privately discloses vulnerability
2. We acknowledge and assess within 48 hours
3. We develop and test a fix
4. We release the fix and publicly disclose (credit given to reporter)
5. If no response to fix within 90 days, reporter may publicly disclose

## Acknowledgments

We appreciate the security researchers and community members who help keep Dotty secure. Security vulnerabilities will be acknowledged in our release notes (unless the reporter requests anonymity).

## Contact

For security-related inquiries only:

- **GitHub Security Advisories**: [Create a new advisory](https://github.com/dominic-codespoti/dotty/security/advisories/new)
- **General Issues**: [GitHub Issues](https://github.com/dominic-codespoti/dotty/issues) (not for security reports)

---

Last updated: April 2026
