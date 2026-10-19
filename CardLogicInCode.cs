///Hey look here...
/// 
/// I'm trying to express with code what I want our boards to do.
/// 
/// I want uses to be able to share and download/upload/update boards/workflows very easily
/// 
/// Maybe I want astra 6.1 to make a manage a bunch of cards and have Luna do the code 
/// and then has sol 6.1 review the code and then call a script to do a PR and a production release.
/// 
/// Maybe I tweaked the prompt and I found the very best code review agent and I want to share it. ect.
/// 


using VibeRails.Services;

public class BaseFeature
{
    public Guid ID { get; set; }
    public int Version { get; set; }
    public string DisplayName { get; set; }
    public string Description { get; set; }
    public List<BaseCard> Cards { get; set; }
    public Queue[BaseAgent]? Agents { get; set; }
}

/// <summary>
/// 1) A card is a unit of work. It holds state and data about something that needs to be done.
/// it could be a bug or a task or a feature or a research spike or anything else.
///  
/// 2) It can be created locally or remotely (i.e. from Jira, Azure DevOps (ADO), the "Board" on VibeRails.ai).
/// 
/// 3) It is different than a Jira or ADO card. In Jira or ADO cards mean whatever they mean in those systems 
/// but local cards represente a unit or work that needs to be done. 
/// Really, the point is:
/// - Cards, lanes, features, whatever in those systems are their own things and we might have some overlap in our local system 
/// but they're not that same and we don't mess with their systems
/// - We reflect the work back into those systems that we do locally as comments on the card but they're 2 different things.
/// - For instance, A card could come from a Jira board with 5 lanes but could go through a local board with 7 lanes 3 times 
/// but we will only reflect that state back into the card...
/// 
/// big picture a card is a unit of work a that will go through a workflow and scripts and "agents" will act based on the work item through a path. 
/// </summary>
public class BaseCard
{
    public Guid ID { get; set; }
    public int Version { get; set; }
    public string DisplayName { get; set; }
    public string Description { get; set; }
    public List<BaseCard> RelatedCards { get; set; }
    public Queue[BaseAgent]? Agents { get; set; }
}

/// <summary>
/// This is an agent they can run... 
/// Which is really just a Terminal with a initial message they set and some config flags set.
/// </summary>
public class BaseAgent
{
    public Guid ID { get; set; }
    public int Version { get; set; }
    public string DisplayName { get; set; }
    public string Description { get; set; }

    public VibeRails.Services.LLM Agent { get; set; }
    public string Prompt { get; set; }
}


/// <summary>
/// This is a script they can run, it has to be run through the same path as the scripts run now.
/// 
/// 
/// NO SHARING SCRIPTS!!!!
/// USERS CAN SHARE A DESCRIPTION OF THE SCRIPT AND ANOTHER USER CAN USE A LLM TO REBUILD FOR THIER OWN NEEDS BUT NO SHARING SCRIPTS AS CODE!
/// 
/// like, as mush as we can try and stop this.... 
/// It's a user safty thign but if they REALLLY want to blindly run a script on their machine someone else wrote we can't stop them... 
/// So, just do our best but don't break our back trying to stop them just reasonable
/// 
/// </summary>
public class BaseAutomation
{
    public Guid ID { get; set; }
    public int Version { get; set; }
    public string DisplayName { get; set; }
    public string Description { get; set; }
    
}



/// <summary>
/// A local board is a workflow. 
/// </summary>

public class BaseLocalBoard
{
    public Guid ID { get; set; }
    public int Version { get; set; }
    public string DisplayName { get; set; }
    public string Description { get; set; }
    public List<BaseCard> Cards { get; set; }
    public List<BaseBoardLane> Lanes { get; set; }
    public Queue[BaseAgent]? Agents { get; set; }
}


public class BaseBoardLane
{

    public Guid ID { get; set; }
    public LaneType Type { get; set; }
    public int Version { get; set; }
    public string DisplayName { get; set; }
    public string Description { get; set; }
    public List<BaseCard> Cards { get; set; }
    public Queue[BaseAgent]? Agents { get; set; }
}
public enum LaneType
{
    NotSet,
    Refinement,
    Planning,
    Ready,
    CodeReview,
    PullRequestReveiw,
    Deployment,
    Done
}


/// <summary>
/// This is refrence to Jira, ADO, or VibeRails.ai or any others we might add. 
/// </summary>

public class BaseRemoteBoard
{

    public Guid ID { get; set; }
    public string DisplayName { get; set; }
    public string Description { get; set; }
}

