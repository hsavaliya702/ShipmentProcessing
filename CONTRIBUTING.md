# Contributing to Shipment Tracking Service

Thank you for your interest in contributing to the Shipment Tracking Service! This document provides guidelines and instructions for contributing to this project.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [Getting Started](#getting-started)
- [Development Environment](#development-environment)
- [Project Structure](#project-structure)
- [Coding Standards](#coding-standards)
- [Testing Requirements](#testing-requirements)
- [Pull Request Process](#pull-request-process)
- [Commit Message Guidelines](#commit-message-guidelines)

## Code of Conduct

This project adheres to a code of conduct. By participating, you are expected to uphold this code. Please be respectful and constructive in all interactions.

## Getting Started

1. **Fork the repository** on GitHub
2. **Clone your fork** locally:
   ```bash
   git clone https://github.com/YOUR_USERNAME/ShipmentProcessing.git
   cd ShipmentProcessing
   ```
3. **Add upstream remote**:
   ```bash
   git remote add upstream https://github.com/hsavaliya702/ShipmentProcessing.git
   ```
4. **Create a feature branch**:
   ```bash
   git checkout -b feature/your-feature-name
   ```

## Development Environment

### Prerequisites

- **.NET 8 SDK** (version 8.0.416 or later)
- **AWS CLI** configured with development credentials
- **Docker** (optional, for LocalStack)
- **Visual Studio 2022** or **VS Code** with C# extensions

### Setup

1. **Install .NET 8 SDK**:
   ```bash
   dotnet --version  # Should show 8.0.x
   ```

2. **Restore NuGet packages**:
   ```bash
   dotnet restore ShipmentTracking.sln
   ```

3. **Build the solution**:
   ```bash
   dotnet build ShipmentTracking.sln
   ```

4. **Run tests**:
   ```bash
   dotnet test ShipmentTracking.sln
   ```

### IDE Configuration

#### Visual Studio 2022
- Install the "AWS Toolkit for Visual Studio"
- Enable "Format document on save" in Tools → Options → Text Editor → C# → Code Style → Formatting

#### VS Code
- Install C# extension (ms-dotnettools.csharp)
- Install AWS Toolkit extension
- Recommended settings in `.vscode/settings.json`:
  ```json
  {
    "editor.formatOnSave": true,
    "omnisharp.enableRoslynAnalyzers": true,
    "omnisharp.enableEditorConfigSupport": true
  }
  ```

## Project Structure

```
ShipmentProcessing/
├── src/
│   ├── ShipmentTracking.Common/          # Shared models and utilities
│   ├── ShipmentTracking.Subscriber/      # Subscriber Lambda
│   └── ShipmentTracking.WebhookProcessor/  # Webhook Processor Lambda
├── tests/
│   ├── ShipmentTracking.Common.Tests/
│   ├── ShipmentTracking.Subscriber.Tests/
│   └── ShipmentTracking.WebhookProcessor.Tests/
├── infrastructure/
│   ├── subscriber-lambda/
│   ├── webhook-processor-lambda/
│   └── shared/
├── docs/
│   ├── subscriber-lambda/
│   ├── webhook-processor-lambda/
│   └── deployment/
├── .editorconfig                         # Code formatting rules
├── Directory.Build.props                 # Shared MSBuild properties
└── global.json                           # .NET SDK version
```

## Coding Standards

### C# Conventions

1. **Follow Microsoft's C# Coding Conventions**:
   - PascalCase for public members, types, and namespaces
   - camelCase for private fields (with `_` prefix)
   - Use `var` when type is obvious from right-hand side

2. **XML Documentation**:
   - All public APIs must have XML documentation comments
   - Include `<summary>`, `<param>`, `<returns>`, and `<exception>` tags
   - Example:
     ```csharp
     /// <summary>
     /// Validates webhook authenticity using HMAC-SHA256.
     /// </summary>
     /// <param name="payload">Raw webhook payload.</param>
     /// <param name="signature">Signature from headers.</param>
     /// <returns>True if valid, false otherwise.</returns>
     public bool ValidateSignature(string payload, string signature)
     ```

3. **Async/Await**:
   - All I/O operations must be async
   - Async methods should be suffixed with `Async`
   - Always accept `CancellationToken` parameter

4. **Error Handling**:
   - Use specific exception types
   - Log errors with correlation IDs
   - Don't swallow exceptions
   - Example:
     ```csharp
     try
     {
         await ProcessWebhookAsync(request, cancellationToken);
     }
     catch (PayloadParsingException ex)
     {
         _logger.LogError(ex, "Failed to parse webhook payload");
         return BadRequest(ex.Message);
     }
     ```

5. **Resource Management**:
   - Use `using` statements or declarations for disposable resources
   - Prefer `using var` for shorter scopes

### EditorConfig

The project includes a `.editorconfig` file that enforces:
- 4-space indentation
- UTF-8 encoding
- LF line endings (except on Windows)
- Trailing whitespace removal
- Final newline

Your IDE should respect these settings automatically.

### Code Analysis

- Enable Roslyn analyzers (configured in `Directory.Build.props`)
- Address all warnings before submitting PR
- Run `dotnet format` to auto-format code:
  ```bash
  dotnet format ShipmentTracking.sln
  ```

## Testing Requirements

### Unit Tests

1. **Coverage Target**: Aim for >80% code coverage
2. **Test Framework**: xUnit with Moq for mocking
3. **Naming Convention**: `MethodName_Scenario_ExpectedResult`
   ```csharp
   [Fact]
   public async Task SubscribeAsync_WithValidTrackingNumber_ReturnsSuccess()
   {
       // Arrange
       var client = new UspsSubscriptionClient(...);
       
       // Act
       var result = await client.SubscribeAsync(request);
       
       // Assert
       Assert.True(result.Success);
   }
   ```

4. **Test Organization**:
   - One test class per production class
   - Group related tests with `[Theory]` and `[InlineData]`
   - Use descriptive test names

### Integration Tests

1. **LocalStack**: Use for AWS service mocking
2. **Test Data**: Store in `tests/test-data/`
3. **Cleanup**: Ensure tests clean up resources

### Running Tests

```bash
# All tests
dotnet test ShipmentTracking.sln

# Specific project
dotnet test src/ShipmentTracking.Subscriber.Tests/

# With coverage
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura
```

## Pull Request Process

### Before Submitting

1. **Sync with upstream**:
   ```bash
   git fetch upstream
   git rebase upstream/main
   ```

2. **Run all tests**:
   ```bash
   dotnet test ShipmentTracking.sln
   ```

3. **Build release configuration**:
   ```bash
   dotnet build -c Release ShipmentTracking.sln
   ```

4. **Format code**:
   ```bash
   dotnet format ShipmentTracking.sln
   ```

### Creating the PR

1. **Push to your fork**:
   ```bash
   git push origin feature/your-feature-name
   ```

2. **Open PR on GitHub**
   - Use a descriptive title
   - Reference related issues (e.g., "Fixes #123")
   - Provide a detailed description of changes
   - Include before/after examples if applicable

3. **PR Template** (add to description):
   ```markdown
   ## Description
   Brief description of changes

   ## Type of Change
   - [ ] Bug fix
   - [ ] New feature
   - [ ] Breaking change
   - [ ] Documentation update

   ## Testing
   - [ ] Unit tests added/updated
   - [ ] Integration tests added/updated
   - [ ] Manual testing performed

   ## Checklist
   - [ ] Code follows project style guidelines
   - [ ] Self-review performed
   - [ ] Documentation updated
   - [ ] No new warnings introduced
   - [ ] Tests pass locally
   ```

### Review Process

1. **Automated Checks**: Must pass all CI/CD checks
2. **Code Review**: At least one approving review required
3. **Address Feedback**: Respond to all review comments
4. **Update as Needed**: Push additional commits to address feedback

### Merging

- **Squash and Merge**: Preferred for feature branches
- **Rebase and Merge**: For linear history (maintainers only)
- **Delete Branch**: After merge

## Commit Message Guidelines

Follow [Conventional Commits](https://www.conventionalcommits.org/):

### Format

```
<type>(<scope>): <subject>

<body>

<footer>
```

### Types

- **feat**: New feature
- **fix**: Bug fix
- **docs**: Documentation changes
- **style**: Code style changes (formatting, etc.)
- **refactor**: Code refactoring
- **test**: Adding/updating tests
- **chore**: Maintenance tasks (dependencies, etc.)
- **perf**: Performance improvements
- **ci**: CI/CD changes

### Examples

```bash
# Feature
feat(subscriber): add UPS carrier support

Implement UPS API integration with authentication and webhook subscription.
Includes error handling and retry logic.

Closes #45

# Bug fix
fix(webhook): correct signature validation for USPS

The timestamp was not being included in the signature computation,
causing all USPS webhooks to fail validation.

Fixes #67

# Documentation
docs(readme): update deployment instructions

Add steps for configuring custom domain and ACM certificate.
```

### Scope Guidelines

- `subscriber`: Subscriber Lambda changes
- `webhook`: Webhook Processor Lambda changes
- `common`: Shared library changes
- `infra`: Infrastructure changes
- `ci`: CI/CD pipeline changes
- `docs`: Documentation changes
- `tests`: Test-only changes

## Additional Guidelines

### Adding New Carriers

When adding support for a new carrier (e.g., UPS, FedEx):

1. Implement `ICarrierSubscriptionClient` in Subscriber Lambda
2. Implement `ICarrierPayloadParser` in Webhook Processor
3. Implement `IWebhookValidator` for signature validation
4. Add status mappings to `StatusTranslationService`
5. Update `CarrierSubscriptionClientFactory`
6. Add unit tests for all implementations
7. Update documentation

### Security Considerations

- Never commit secrets or credentials
- Use AWS Secrets Manager for all sensitive data
- Validate all external inputs
- Use parameterized queries for DynamoDB
- Follow OWASP guidelines

### Performance Guidelines

- Minimize cold start time (avoid heavy initialization)
- Use async/await for all I/O
- Cache AWS Secrets Manager responses
- Use DynamoDB conditional writes efficiently
- Monitor Lambda duration and memory usage

## Getting Help

- **Documentation**: Check [docs/](./docs/) directory
- **Issues**: Search [existing issues](https://github.com/hsavaliya702/ShipmentProcessing/issues)
- **Discussions**: Use [GitHub Discussions](https://github.com/hsavaliya702/ShipmentProcessing/discussions)

## Recognition

Contributors will be acknowledged in:
- GitHub contributors page
- Release notes
- Project documentation

Thank you for contributing! 🎉
