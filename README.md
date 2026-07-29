# vslhq26-ItsRhysNotRhys

# Modernization Engine ⚙️

**VSLive! Microsoft AI Hackathon 2026 Submission**  
**Category:** Best AI Agent or Workflow Automation / Best Azure OpenAI App

## 🚀 Overview

The **Modernization Engine** is a three-tiered, AI-powered pipeline designed to migrate legacy enterprise code (e.g., synchronous, blocking operations) into modern, asynchronous frameworks. 

Unlike standard LLM wrappers that suffer from token bloat, context window limits, and API hallucinations, this engine uses a deterministic local scanner to isolate problems, live Model Context Protocol (MCP) servers to fetch current documentation, and an automated compilation loop to self-heal generated code.

## 🏗️ System Architecture

1. **Local Engine (Roslyn AST):** Scans local C# files to deterministically identify anti-patterns (e.g., `Thread.Sleep`, `.Result`) and extracts *only* the offending syntax nodes, ensuring proprietary enterprise codebases aren't dumped entirely into an LLM.
2. **Context & AI Transformation (Context7 + OpenAI):** Uses the **Context7 MCP Server** to fetch up-to-date, version-specific .NET API documentation. This context is injected into an OpenAI Structured Outputs payload to guarantee type-safe, zero-hallucination JSON responses.
3. **Verification & Self-Healing (Local Compiler):** The modernized code is swapped back into the syntax tree, written to disk, and automatically passed to the `dotnet build` toolchain. If compilation fails, the `stderr` output is fed back to the LLM for an autonomous self-healing pass.

## 🛠️ Prerequisites

*   [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
*   [Node.js / npx](https://nodejs.org/) (Required for the local Context7 MCP server execution)

## 🔐 Setup & Configuration

This application uses the .NET Secret Manager to securely handle API keys without committing them to source control. 

1. Clone the repository and navigate to the project directory:
   ```bash
   git clone <your-repo-url>
   cd ModernizationEngineUI
   ```

2. Initialize user secrets for the project:
   ```bash
   dotnet user-secrets init
   ```

3. Set your API keys using the following commands:
   ```bash
   # Required: Set your OpenAI API Key for code generation
   dotnet user-secrets set "OpenAI:ApiKey" "YOUR-KEY"
   
   # Required: Set your Context7 API Key for live documentation grounding
   dotnet user-secrets set "Context7:ApiKey" "YOUR_KEY"
   ```

## 🏃‍♂️ Running the Application

This project features an interactive Blazor Server dashboard with an embedded Monaco Diff Editor to visualize the agentic refactoring process in real-time.

1. Start the application:
   ```bash
   dotnet run
   ```
2. Open your browser and navigate to `http://localhost:5077` (or the port specified in your console output).
3. Click **"Run Agentic Refactor"** to watch the engine parse the AST, fetch MCP context, generate code, and verify compilation locally.
