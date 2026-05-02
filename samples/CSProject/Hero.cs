namespace Game;

public class Hero : Unit
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="name"></param>
    /// <param name="hp"></param>
    public Hero(string name, double hp) : base(name, hp)
    {
        Name = "H" + name;
        HP = hp * 2;
    }

    //
    /**
    */
    public override void LevelUp()
    {
        base.LevelUp();
        HP += 10;
    }

    public override string ToString()
    {
        return $"Hero: {Name} - HP: {HP}";
    }
}
