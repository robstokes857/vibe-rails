public static class UserWaiting
{
    public static bool Check(string input)
    {
        if(input.Contains('•') && input.Contains('◦'))
        {
            return true;
        }
        return false;
    }
}
