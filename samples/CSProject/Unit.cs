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

    private void Update()
    {
        BJDebugMsg("Tick");
    }

    private async SFLib.Async.Task TheExcitingPart()
    {
        while (true)
        {
            await SFLib.Async.Task.Delay(1000);
            Update();
        }
    }

    public virtual void LevelUp()
    {
        BJDebugMsg("Level Up!");

        var messages = new SFLib.Collections.List<string>();
        messages.Add("You have leveled up!");
        foreach (var message in messages)
        {
            BJDebugMsg(message);
        }

        for (int i = 0; i < messages.Count;)
        {
            BJDebugMsg(messages[i]);
            if (i < 10)
            {
                i++;
            }
        }

        var dict = new SFLib.Collections.Dictionary<string, int>();
        dict["Level"] = 2;
        dict["HP"] = 66;
        BJDebugMsg($"Level: {dict["Level"]}, HP: {dict["HP"]}");
        foreach (var p in dict)
        {
            var pair = p;
            BJDebugMsg($"{p.Key}: {p.Value}");
            var keyValue = pair.Key;
        }
    }

    public override string ToString()
    {
        return $"{Name} - HP: {HP}";
    }
}
