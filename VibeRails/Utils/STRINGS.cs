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

        public const string AUTH_BOOTSTRAP_HTML = @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Setting up session...</title>
    <style>
        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, sans-serif;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            color: white;
        }
        .container {
            text-align: center;
            padding: 2rem;
        }
        h1 {
            font-size: 2.5rem;
            font-weight: 300;
            margin-bottom: 1rem;
            animation: fadeIn 0.5s ease-in;
        }
        p {
            font-size: 1.1rem;
            opacity: 0.9;
            animation: fadeIn 0.5s ease-in 0.2s both;
        }
        .spinner {
            width: 50px;
            height: 50px;
            border: 4px solid rgba(255, 255, 255, 0.3);
            border-top-color: white;
            border-radius: 50%;
            animation: spin 0.8s linear infinite;
            margin: 2rem auto;
        }
        @keyframes spin {
            to { transform: rotate(360deg); }
        }
        @keyframes fadeIn {
            from { opacity: 0; transform: translateY(-10px); }
            to { opacity: 1; transform: translateY(0); }
        }
    </style>
</head>
<body>
    <div class=""container"">
        <h1>Setting up your session</h1>
        <div class=""spinner""></div>
        <p>Redirecting...</p>
    </div>
    <script>
        // Wait 1 second to show the auth screen, then redirect
        setTimeout(() => {
            window.location.replace('/');
        }, 1000);
    </script>
</body>
</html>";


        public const string AUTH_INVALID_CODE_HTML = @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Invalid Bootstrap Code</title>
    <style>
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, sans-serif;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            color: white;
            text-align: center;
            padding: 2rem;
        }
        h1 { font-size: 2rem; margin-bottom: 1rem; }
        p { font-size: 1.1rem; opacity: 0.9; }
    </style>
</head>
<body>
    <div>
        <h1>Invalid or Expired Link</h1>
        <p>This authentication link is invalid, has expired, or has already been used.</p>
        <p>Please start the application from the command line to get a new link.</p>
    </div>
</body>
</html>";


        public const string AUTH_REQUIRED_HTML = @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Authentication Required</title>
    <style>
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, sans-serif;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            color: white;
            text-align: center;
            padding: 2rem;
        }
        h1 { font-size: 2rem; margin-bottom: 1rem; }
        p { font-size: 1.1rem; opacity: 0.9; margin-bottom: 0.5rem; }
    </style>
</head>
<body>
    <div>
        <h1>Authentication Required</h1>
        <p>Please start the application from the command line.</p>
        <p>The CLI will provide a secure authentication link.</p>
    </div>
</body>
</html>";

    }
}
