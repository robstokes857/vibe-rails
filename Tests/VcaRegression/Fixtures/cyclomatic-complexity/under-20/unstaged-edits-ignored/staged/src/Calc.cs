namespace Fixture;

public static class Calc
{
    public static int Run(int[] values)
    {
        var total = 0;
        if (values[0] > 0) { total += 1; }
        if (values[1] > 0) { total += 2; }
        if (values[2] > 0) { total += 3; }
        return total;
    }
}
