public static class UserWaiting
{
    public static bool Check(string input)
    {
        if(input.Contains('•') && input.Contains('◦'))
        {
            if (input.Contains("ing"))
                return false;
            return true;
        }
        return false;
    }
}
