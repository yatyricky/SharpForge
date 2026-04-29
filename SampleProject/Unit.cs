using SharpLib;
namespace Game;


public class Unit
{
    public string Name;
    public double HP;

    public Unit(string name, double hp)
    {
        Name = name;
        HP = hp;
    }

    public virtual void LevelUp()
    {
        BJDebugMsg("Level Up!");

        var messages = new SharpLib.List<string>();
        messages.Add("You have leveled up!");
        BJDebugMsg(messages[0]);

        var dict = new Dictionary<string, int>();
        dict["Level"] = 2;
        dict["HP"] = 66;
        BJDebugMsg($"Level: {dict["Level"]}, HP: {dict["HP"]}");
        foreach (var p in dict)
        {
            BJDebugMsg($"{p.Key}: {p.Value}");
        }
    }

    public override string ToString()
    {
        return $"{Name} - HP: {HP}";
    }
}
