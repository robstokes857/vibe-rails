using VibeRails.DTOs;

namespace VibeRails.Routes;

public static class SwarmRoutes
{
    public static void Map(WebApplication app)
    {
        // POST /api/v1/swarm/plan - accept user prompt and return mock swarm plan JSON
        app.MapPost("/api/v1/swarm/plan", (SwarmPlanRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new ErrorResponse("Message is required"));
            }

            var plan = BuildMockPlan();
            return Results.Ok(plan);
        }).WithName("CreateSwarmPlan");
    }

    private static SwarmPlanResponse BuildMockPlan()
    {
        return new SwarmPlanResponse(
            Name: "Swarm Build Plan",
            Description: "Break the coding task into focused streams that can run in parallel across LLM CLIs.",
            Steps:
            [
                new SwarmStepResponse(
                    Name: "Define Scope",
                    Description: "Capture clear acceptance criteria and break work into streams.",
                    Groups:
                    [
                        new SwarmGroupResponse(
                            Name: "Discovery",
                            Description: "Understand existing app boundaries and required touchpoints.",
                            Color: "#3b82f6",
                            Tasks:
                            [
                                new SwarmTaskResponse("Audit current routes", "List backend route files and map insertion points."),
                                new SwarmTaskResponse("Audit current UI views", "Identify nav + template locations for Swarm.")
                            ]
                        ),
                        new SwarmGroupResponse(
                            Name: "Planning",
                            Description: "Convert requirements into implementation slices.",
                            Color: "#10b981",
                            Tasks:
                            [
                                new SwarmTaskResponse("Define API contract", "Finalize request/response shape for mock integration."),
                                new SwarmTaskResponse("Define UI states", "Loading, preview, accepted-plan rendering states.")
                            ]
                        )
                    ]
                ),
                new SwarmStepResponse(
                    Name: "Implement",
                    Description: "Add the Swarm view, backend endpoint, and response rendering.",
                    Groups:
                    [
                        new SwarmGroupResponse(
                            Name: "Frontend",
                            Description: "Implement Swarm view and plan visualizer integration.",
                            Color: "#f59e0b",
                            Tasks:
                            [
                                new SwarmTaskResponse("Add Swarm nav/view", "Create subnav item and template wiring."),
                                new SwarmTaskResponse("Submit prompt to API", "POST user prompt and parse response."),
                                new SwarmTaskResponse("Render returned plan", "Display preview and full plan visualizer.")
                            ]
                        ),
                        new SwarmGroupResponse(
                            Name: "Backend",
                            Description: "Provide mock plan endpoint for current phase.",
                            Color: "#ef4444",
                            Tasks:
                            [
                                new SwarmTaskResponse("Add Swarm route", "Create /api/v1/swarm/plan endpoint."),
                                new SwarmTaskResponse("Return mock JSON", "Serve static test plan payload.")
                            ]
                        )
                    ]
                ),
                new SwarmStepResponse(
                    Name: "Validate",
                    Description: "Verify submit flow and JSON rendering are stable.",
                    Groups:
                    [
                        new SwarmGroupResponse(
                            Name: "Smoke Test",
                            Description: "Confirm full loop from input to visualized plan.",
                            Color: "#8b5cf6",
                            Tasks:
                            [
                                new SwarmTaskResponse("Submit sample prompt", "Ensure API returns expected mock JSON."),
                                new SwarmTaskResponse("Accept and view plan", "Confirm plan view interactions work end-to-end.")
                            ]
                        )
                    ]
                )
            ]
        );
    }
}
