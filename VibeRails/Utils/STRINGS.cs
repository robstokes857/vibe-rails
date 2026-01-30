namespace VibeRails.Services
{
    public static class STRINGS
    {
        public const string INIT_AGENT_FILE_CONTENT =
@$"# {AGENT_FILE_HEADER}

## Vibe Rails Rules

## Files

";


        public const string AGENT_FILE_HEADER =
            @"# Repository Guidelines

This file provides instructions and context for humans and AI coding assistants working in this codebase.

## Development Guidelines
- Read AGENTS.md for detailed agent instructions and information about this project
- Follow the rules and guidelines specified in this file exactly
- Follow existing code patterns and conventions in the codebase
- Keep changes focused and minimal";



        public const string RULE_HEADER = "## Vibe Rails Rules";
        public const string FILE_HEADER = "## Files";

    }
}
