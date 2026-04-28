namespace Game;

public class Hero
{
    public string Name;
    public double HP;

    public Hero(string name, double hp)
    {
        Name = name;
        HP = hp;
    }

    public void LevelUp()
    {
        HP += 10;
    }

    public override string ToString()
    {
        return $"{Name} - HP: {HP}";
    }
}
