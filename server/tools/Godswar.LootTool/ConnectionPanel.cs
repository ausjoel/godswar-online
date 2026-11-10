namespace Godswar.LootTool;

/// <summary>
/// Connection bar: the operator types host, port, database, user and password
/// separately, then connects and presses "one click read". Mirrors the layout of
/// the reference loot editor.
/// </summary>
internal sealed class ConnectionPanel : UserControl
{
    private readonly TextBox _host = new();
    private readonly TextBox _port = new();
    private readonly TextBox _database = new();
    private readonly TextBox _username = new();
    private readonly TextBox _password = new();
    private readonly TextBox _clientRoot = new();
    private readonly Label _status = new();

    public ConnectionPanel()
    {
        Dock = DockStyle.Fill;
        Height = 76;
        Build();
    }

    public event EventHandler? ConnectRequested;

    public event EventHandler? ReadRequested;

    public event EventHandler? ClientRootBrowsed;

    public void LoadFrom(LootToolSettings settings)
    {
        _host.Text = settings.Host;
        _port.Text = settings.Port.ToString();
        _database.Text = settings.Database;
        _username.Text = settings.Username;
        _password.Text = settings.Password;
        _clientRoot.Text = settings.ClientRoot;
    }

    public void ReadInto(LootToolSettings settings)
    {
        settings.Host = _host.Text.Trim();
        settings.Port = int.TryParse(_port.Text.Trim(), out var port) ? port : settings.Port;
        settings.Database = _database.Text.Trim();
        settings.Username = _username.Text.Trim();
        settings.Password = _password.Text;
        settings.ClientRoot = _clientRoot.Text.Trim();
    }

    public void SetStatus(string text, bool healthy)
    {
        _status.Text = text;
        _status.ForeColor = healthy ? Color.SeaGreen : Color.Firebrick;
    }

    public void SetBusy(bool busy) => UseWaitCursor = busy;

    private void Build()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        var fields = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        fields.Controls.Add(MakeLabel("主机"));
        fields.Controls.Add(MakeBox(_host, 105));
        fields.Controls.Add(MakeLabel("端口"));
        fields.Controls.Add(MakeBox(_port, 55));
        fields.Controls.Add(MakeLabel("数据库"));
        fields.Controls.Add(MakeBox(_database, 130));
        fields.Controls.Add(MakeLabel("用户名"));
        fields.Controls.Add(MakeBox(_username, 90));
        fields.Controls.Add(MakeLabel("密码"));
        _password.UseSystemPasswordChar = true;
        fields.Controls.Add(MakeBox(_password, 110));
        root.Controls.Add(fields, 0, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        var connect = new Button { Text = "连接数据库", Width = 100, Height = 26 };
        connect.Click += (_, _) => ConnectRequested?.Invoke(this, EventArgs.Empty);
        var read = new Button { Text = "一键读取数据", Width = 110, Height = 26 };
        read.Click += (_, _) => ReadRequested?.Invoke(this, EventArgs.Empty);
        actions.Controls.Add(connect);
        actions.Controls.Add(read);
        actions.Controls.Add(MakeLabel("客户端目录"));
        actions.Controls.Add(MakeBox(_clientRoot, 260));
        var browse = new Button { Text = "浏览…", Width = 66, Height = 26 };
        browse.Click += (_, _) => ClientRootBrowsed?.Invoke(this, EventArgs.Empty);
        actions.Controls.Add(browse);
        _status.AutoSize = true;
        _status.Padding = new Padding(12, 8, 0, 0);
        actions.Controls.Add(_status);
        root.Controls.Add(actions, 0, 1);

        Controls.Add(root);
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Padding = new Padding(8, 8, 2, 0)
    };

    private static TextBox MakeBox(TextBox box, int width)
    {
        box.Width = width;
        box.Margin = new Padding(0, 4, 0, 0);
        return box;
    }
}
