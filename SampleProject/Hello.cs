namespace Game;

public static class Hello
{
    public static int Greet(int n)
    {
        if (n > 0)
        {
            return n + 1;
        }
        return 0;
    }

    public static void Main()
    {
        var hero = new Hero("Arthur", 100);
        BJDebugMsg(hero.ToString());
        hero.LevelUp();
        BJDebugMsg(hero.ToString());
    }
}
