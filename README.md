# Vibe-Rails

**Vibe-Rails** is an opinionated framework that helps keep AI coding assistants from going off the rails.

**Live Site**: [https://viberails.ai/](https://viberails.ai/)

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![TypeScript](https://img.shields.io/badge/TypeScript-5.9.3-3178C6)](https://www.typescriptlang.org/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

---

## Overview
- **Environment Isolation** - Like Conda for LLMs. Create separate environments to experiment with Claude, Codex, or Gemini settings without breaking your primary setup
- **Cross-LLM Learning** - Share context and learnings between different LLM providers (Claude, Codex, Gemini)
- **RAG (Without The Rot) For Your Code** - Track things like repeated fixes the LLM forgets, including when you have to tell it the same thing 6 or 7 times in one session and it still doesn't understand, how you describe a feature and where that code lives, and file change summaries with commits, then only provide what’s useful at call time to prevent context rot.
- **Few Shot Prompting** - Get Gemini or codex to code like Claude for code that has been done before with few shot prompting... Making them up to 20% better (research paper and eval data coming soon.)
- **Rule Enforcement** - Define and enforce coding standards like test coverage, cyclomatic complexity, logging practices, and more. LLMs fix their errors before code can be pushed or before the tech debt get astronomical.
- **Token Savings** - Learn your codebase and how you describe it, providing LLMs with smart file hints to reduce token usage and costs
- **AGENTS.md Management** - Create and manage agent instruction files following the [agents.md specification](https://agents.md/)

---

## Status
- This repo is a lightweight, local-focused version of my personal setup. I'm stripping out multi-GPU/cluster support, heavy eval tooling, and other framework dependencies so it runs fast with Claude, Codex, and Gemini CLIs. I'm rebuilding it around the features I think most people will actually want for local workflows.

## Quick Start

### Prerequisites

**For End Users:**
- Just install from VS Code Marketplace... That's it
- One or more LLM CLIs: Claude CLI, OpenAI Codex, or Google Gemini CLI
- **No other dependencies required** - binaries are bundled in the extension

**For Contributors (Working on the Project):**
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or later (required to build the backend)
- [Node.js 20+](https://nodejs.org/) (required to build the VS Code extension)
- Git
- VS Code 1.85.0 or later

### Installation

#### Build backend

```bash
# Clone the repository
git clone https://github.com/robstokes857/vibe-rails.git
cd vibe-rails

# Build and run
cd VibeRails
dotnet run
```

The dashboard will open in your default browser at `http://localhost:{port 5000-5999}`.

#### Option 2: VS Code Extension

```bash
# Navigate to extension directory
cd vscode-viberails

# Install dependencies
npm install

# Compile TypeScript
npm run compile

# Open in VS Code
code .
```

**Maintained By**: Robert Stokes
