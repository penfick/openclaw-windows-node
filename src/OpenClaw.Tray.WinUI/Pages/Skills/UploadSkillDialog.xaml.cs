using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Services;
using System;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>上传已安装技能到公司市场：收集元数据 → 打包 baseDir → CompanySkillsHub.UploadAsync。</summary>
public sealed partial class UploadSkillDialog : ContentDialog
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private string _baseDir = "";

    public UploadSkillDialog()
    {
        InitializeComponent();
    }

    /// <summary>用技能信息预填表单。</summary>
    internal void Setup(string name, string slug, string description, string baseDir)
    {
        _baseDir = baseDir ?? "";
        SlugBox.Text = string.IsNullOrEmpty(slug) ? name : slug;
        NameBox.Text = name ?? "";
        DescBox.Text = description ?? "";
        VersionBox.Text = "1.0.0";
    }

    private async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var slug = SlugBox.Text.Trim();
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(name))
        {
            args.Cancel = true;
            ShowError("Slug 和名称不能为空。");
            return;
        }

        var hub = CurrentApp.CompanySkillsHub;
        if (hub == null) { args.Cancel = true; ShowError("公司市场客户端未初始化。"); return; }

        var deferral = args.GetDeferral();
        args.Cancel = true; // 阻止默认关闭，上传完再决定
        try
        {
            UpProgress.IsActive = true;
            ProgressRow.Visibility = Visibility.Visible;
            UpStatus.Text = "正在打包技能目录…";
            ErrorText.Visibility = Visibility.Collapsed;

            var zip = await Task.Run(() => SkillUploader.PackSkill(_baseDir));
            UpStatus.Text = $"正在上传（{zip.Length / 1024} KB）…";

            // 带 OA 用户信息（作者/部门），让公司市场归属正确
            var user = CurrentApp.AppState?.AuthState?.UserInfo;
            int? deptId = int.TryParse(user?.DepartmentId, out var d) ? d : null;
            var meta = new CompanySkillUploadMeta(
                Slug: slug,
                Name: name,
                Description: string.IsNullOrEmpty(DescBox.Text.Trim()) ? null : DescBox.Text.Trim(),
                Version: string.IsNullOrEmpty(VersionBox.Text.Trim()) ? null : VersionBox.Text.Trim(),
                CategoryId: null,
                AuthorId: user?.UserId,
                AuthorName: user?.DisplayName,
                DeptId: deptId,
                DeptName: user?.DepartmentName);

            await hub.UploadAsync(zip, meta);

            // 上传成功 → 关闭弹框
            args.Cancel = false;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            UpProgress.IsActive = false;
            deferral.Complete();
        }
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
        ProgressRow.Visibility = Visibility.Collapsed;
    }
}
