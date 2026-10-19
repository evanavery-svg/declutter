using Declutter.Core.Files;
using Declutter.Core.Startup;

namespace Declutter.Core.Tests;

public class ScheduledTaskXmlTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Tasks>
        <!-- \Adobe Acrobat Update Task -->
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Author>Adobe Systems Incorporated</Author><URI>\Adobe Acrobat Update Task</URI></RegistrationInfo>
          <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger><CalendarTrigger /></Triggers>
          <Settings><Enabled>true</Enabled></Settings>
          <Actions Context="Author"><Exec><Command>C:\Program Files (x86)\Common Files\Adobe\ARM\1.0\AdobeARM.exe</Command></Exec></Actions>
        </Task>
        <!-- \Vendor\Helper -->
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers><BootTrigger /></Triggers>
          <Settings><Enabled>false</Enabled></Settings>
          <Actions><Exec><Command>"C:\Vendor\helper.exe"</Command><Arguments>/quiet</Arguments></Exec></Actions>
        </Task>
        <!-- \Nightly Backup -->
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers><CalendarTrigger /></Triggers>
          <Actions><Exec><Command>backup.exe</Command></Exec></Actions>
        </Task>
        <!-- \Disabled Trigger -->
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers><LogonTrigger><Enabled>false</Enabled></LogonTrigger></Triggers>
          <Actions><Exec><Command>x.exe</Command></Exec></Actions>
        </Task>
        <!-- \Microsoft\Windows\Defrag\ScheduledDefrag -->
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers><BootTrigger /></Triggers>
          <Actions><Exec><Command>defrag.exe</Command></Exec></Actions>
        </Task>
        </Tasks>
        """;

    [Fact]
    public void ParsesOnlyThirdPartyStartupTasks()
    {
        var tasks = ScheduledTaskXml.ParseStartupTasks("INFO: some preamble\r\n" + Sample);

        Assert.Equal(2, tasks.Count);

        var adobe = tasks[0];
        Assert.Equal(@"\Adobe Acrobat Update Task", adobe.Path);
        Assert.Equal("Adobe Acrobat Update Task", adobe.Name);
        Assert.True(adobe.Enabled);
        Assert.Equal("\"C:\\Program Files (x86)\\Common Files\\Adobe\\ARM\\1.0\\AdobeARM.exe\"", adobe.Command);
        Assert.Equal("Adobe Systems Incorporated", adobe.Author);

        var helper = tasks[1];
        Assert.Equal(@"\Vendor\Helper", helper.Path);
        Assert.Equal("Helper", helper.Name);
        Assert.False(helper.Enabled);
        Assert.Equal("\"C:\\Vendor\\helper.exe\" /quiet", helper.Command);
    }

    [Fact]
    public void ABrokenTask_DoesNotHideTheOthers()
    {
        string broken = Sample.Replace("<!-- \\Vendor\\Helper -->", "<!-- \\Bad -->\n<Task><Unclosed></Task>\n<!-- \\Vendor\\Helper -->");
        Assert.Equal(2, ScheduledTaskXml.ParseStartupTasks(broken).Count);
    }

    [Fact]
    public void SingleTaskOutput_WithoutTasksWrapper_IsParsed()
    {
        const string single = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><URI>\Solo</URI></RegistrationInfo>
              <Triggers><LogonTrigger /></Triggers>
              <Actions><Exec><Command>solo.exe</Command></Exec></Actions>
            </Task>
            """;
        var task = Assert.Single(ScheduledTaskXml.ParseStartupTasks(single));
        Assert.Equal(@"\Solo", task.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ERROR: Access is denied.")]
    [InlineData("<Tasks><Task>")]
    public void BadOutput_YieldsNothing(string xml) => Assert.Empty(ScheduledTaskXml.ParseStartupTasks(xml));

    [Fact]
    public void FolderMeasure_SumsFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "declutter-measure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "a"));
        File.WriteAllBytes(Path.Combine(dir, "x"), new byte[10]);
        File.WriteAllBytes(Path.Combine(dir, "a", "y"), new byte[5]);
        try
        {
            Assert.Equal(15, FolderMeasure.SizeOf(dir));
            Assert.Null(FolderMeasure.SizeOf(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
