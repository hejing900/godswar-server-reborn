using System.Text;

namespace Godswar.LootTool;

/// <summary>
/// 「导出/导入」页签：把工具配过的东西（标记点 + GM 刷怪点 + 属性覆盖）整体存成一份
/// JSON，或者把一份 JSON 整份写回来。
/// </summary>
/// <remarks>
/// <para>
/// 导入按**天然键** upsert（标记点/刷怪点按名字，属性按 map+object 或 map+template），
/// 所以同一份文件导入两次结果一样，也不会打乱目标库的自增序列。
/// </para>
/// <para>
/// 包体（base64）随文件一起走，换机器不用先有同样的已发布内容；但**模板仍必须在该图
/// 已发布**，否则服务端会拒绝那个刷怪点 —— 那是内容侧的不变量，工具绕不过去。
/// </para>
/// </remarks>
internal sealed class ExportPanel : UserControl, IAsyncDisposable
{
    private const string NotConnectedMessage = "请先在顶部连接数据库。";

    private readonly GmContentStore _store = new();
    private readonly Label _summary = new();
    private readonly TextBox _log = new();
    private readonly StatusLabel _status = new();
    private bool _connected;
    private bool _busy;

    public ExportPanel()
    {
        Dock = DockStyle.Fill;
        Controls.Add(BuildUi());
    }

    public void SetStatus(string text) => SetStatus(text, error: false);

    public void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.Firebrick : Color.DimGray;
    }

    public async Task ConnectAndReadAsync(string connectionString)
    {
        _store.Connect(connectionString);
        _connected = true;
        await RefreshAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
    }

    public async Task RefreshAsync()
    {
        if (!_connected || _busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var bundle = await _store.ExportAsync();
            _summary.Text = $"当前库 {_store.DatabaseName}：{bundle.Summary}";
            SetStatus("就绪。导出的文件里含包体，换机器可直接导入。");
        }
        catch (Exception error)
        {
            SetStatus($"读取失败：{error.Message}", error: true);
        }
        finally
        {
            _busy = false;
        }
    }

    private Control BuildUi()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(8)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        _summary.Dock = DockStyle.Fill;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_summary, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.Add(MakeButton("导出到 JSON…", ExportAsync, 130));
        actions.Controls.Add(MakeButton("从 JSON 导入…", ImportAsync, 130));
        actions.Controls.Add(MakeButton("刷新", RefreshAsync, 70));
        layout.Controls.Add(actions, 0, 1);

        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = "导入是整份事务，按名字/键覆盖同名项；服务端要重启才会读到新内容。"
        }, 0, 2);

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font(FontFamily.GenericMonospace, 9f);
        layout.Controls.Add(_log, 0, 3);

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Text = NotConnectedMessage;
        layout.Controls.Add(_status, 0, 4);
        return layout;
    }

    private Button MakeButton(string text, Func<Task> action, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 28 };
        button.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception error)
            {
                SetStatus(error.Message, error: true);
                Append($"失败：{error.Message}");
            }
        };
        button.Margin = new Padding(0, 4, 8, 0);
        return button;
    }

    private async Task ExportAsync()
    {
        if (!_connected)
        {
            SetStatus(NotConnectedMessage, error: true);
            return;
        }

        var bundle = await _store.ExportAsync();
        using var dialog = new SaveFileDialog
        {
            Title = "导出 GM 配置",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = $"gm-content-{_store.DatabaseName}-" +
                       $"{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            SetStatus("已取消导出。");
            return;
        }

        await File.WriteAllTextAsync(
            dialog.FileName,
            GmContentStore.Serialize(bundle),
            new UTF8Encoding(false));
        Append($"导出 → {dialog.FileName}");
        Append($"  {bundle.Summary}");
        SetStatus($"已导出：{bundle.Summary}");
    }

    private async Task ImportAsync()
    {
        if (!_connected)
        {
            SetStatus(NotConnectedMessage, error: true);
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "导入 GM 配置",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            SetStatus("已取消导入。");
            return;
        }

        GmContentBundle bundle;
        try
        {
            bundle = GmContentStore.Deserialize(
                await File.ReadAllTextAsync(dialog.FileName));
        }
        catch (Exception error)
        {
            SetStatus($"读不了这份文件：{error.Message}", error: true);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"把这份文件导入 {_store.DatabaseName}？\n\n{bundle.Summary}\n" +
            $"（来自 {bundle.Database}，导出于 {bundle.ExportedAt.ToLocalTime():yyyy-MM-dd HH:mm}）\n\n" +
            "同名/同键的项会被文件里的内容覆盖。",
            "确认导入",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            SetStatus("已取消导入。");
            return;
        }

        var result = await _store.ImportAsync(bundle, Environment.UserName);
        Append($"导入 ← {dialog.FileName}");
        Append($"  {result.Summary}");
        await RefreshAsync();
        SetStatus(result.Summary + " 重启服务端后生效。");
    }

    private void Append(string text)
    {
        _log.AppendText(text + Environment.NewLine);
    }

    private sealed class StatusLabel : Label
    {
        public StatusLabel()
        {
            AutoSize = false;
            Height = 22;
        }
    }
}
