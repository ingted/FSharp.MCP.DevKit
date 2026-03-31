# FSharp.MCP.DevKit Documentation

## Table of Contents

### System Documentation
- **[Architecture](./Architecture.md)** - System design, components, and communication mechanisms
- **[Features](./Features.md)** - Feature documentation with implementation details
- **[Code Editing](./FSharpCodeEditing.md)** - Code manipulation tools and safety features
- **[Known Issues](./ISSUES.md)** - Current bugs, limitations, and tracking
- **[.NET Tool Usage](./DOTNET_TOOL_USAGE.md)** - Installation and usage as .NET tool

### Project-Specific Documentation
- **[All Projects](./projects/README.md)** - Overview of all projects in the solution
  - [Core](./projects/Core/) - Foundation layer and FSI session management
  - [Analysis](./projects/Analysis/) - Code analysis and symbol detection
  - [Communication](./projects/Communication/) - Named pipe IPC infrastructure
  - [CodeEditing](./projects/CodeEditing/) - Safe code manipulation and formatting
  - [Server](./projects/Server/) - MCP server implementation and tools
  - [Documentation](./projects/Documentation/) - API documentation generation

### Agent Development Strategies
- **[Agent-Instructions-Strategies](./Agent-Instructions-Strategies/)** - Development approach guides
  - [REPL-Driven](./Agent-Instructions-Strategies/REPL-Driven-Default/)
  - [Script-Driven](./Agent-Instructions-Strategies/Script-Driven-Default/)
  - [Signature-Driven](./Agent-Instructions-Strategies/Signature-Driven-Default/)
  - [Multi-Agent Roles](./Agent-Instructions-Strategies/Multi-Agents-Roles/)

### Operational Documentation
- **[Runbook](../doc/Runbook.md)** - Agent 使用指南（建 host、建 session、執行程式碼）
- **[SA](../doc/SA.md)** - System Analysis
- **[SD](../doc/SD.md)** - System Design
