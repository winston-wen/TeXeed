using System.Windows;
using System.Windows.Controls;
using TeXeed.App;
using TeXeed.App.AI;
using TeXeed.App.Controls;
internal static partial class Program
{
    private static async Task CheckAiHeaderAsync(MainWindow owner,string output)
    {
        using var panel=new AiPanel(new AiConnection(new AiFakeHandler()));
        await panel.SetProjectAsync(System.IO.Path.Combine(output,"header-removed.tex"));
        foreach(var name in new[]{"ChatHeader","HeadingPeek","ConversationTitle","ChatSettingsButton","HistoryButton"})
            if(panel.FindName(name) is not null)throw new Exception("Removed header element remains: "+name);
        if(new AiConversation{Title=new string('汉',16)}.TabTitle!=new string('汉',15)+"…")throw new Exception("Tab title truncation failed");
        Console.WriteLine("AI header replacement: removed body header and 15-character tab title passed.");
    }
}
