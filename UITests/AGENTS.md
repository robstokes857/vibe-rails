# UI Testing Suite

This directory contains the [Playwright](https://playwright.dev/) end-to-end testing suite for the VibeRails frontend.

## Prerequisites

- [Node.js](https://nodejs.org/) installed on your system.

## Setup

Before running tests for the first time, install the dependencies:

```powershell
# Install npm packages
npm install

# Install browser binaries
npx playwright install chromium
```

## Running Tests

### Standard Run
Run all tests in headless mode (console output):
```powershell
npx playwright test
```

### UI Mode (Recommended for Debugging)
Open the interactive Playwright UI to see tests running step-by-step:
```powershell
npx playwright test --ui
```

### View Report
If a test fails, you can view the detailed HTML report:
```powershell
npx playwright show-report
```

## Configuration

- **Target Files:** The tests serve files directly from ../VibeRails/wwwroot.
- **Server:** Uses http-server on port 8080.
- **Logic:** Tests are located in the ./tests directory.

## Adding New Tests

When adding features to pp.js or index.html, add a corresponding spec file in ./tests/*.spec.js to ensure the UI interactions remain functional.
