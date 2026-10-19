///I'm adding thoughts here.


/// <summary>
/// A card is a unit of work. It holds the state and information about something that needs to be done. 
/// It is local only , becuase the work is all done locally.
/// It can be created remotely and pushed locally.
/// It can be pulled from Jira or ADO
/// </summary>
public class BaseCard
{
    public Guid ID { get; set; }
    public int CardVersion { get; set; }
    public string Name { get; set; }    
    public string Description { get; set; }
    //all the other common card attributes
}

public class LocalBoard
{
     public Guid ID { get; set; }
    public string Name { get; set; }    
    public string Description { get; set; }
    public List<BaseCard> Cards {get;set;}
}

