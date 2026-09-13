using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace fyserver;

/// <summary>
/// Razor 后台页面注册：Pages/ 下的页面统一挂在 /admin 前缀下
/// （Pages/Index → /admin/index，Pages/Users → /admin/users …）。
/// 单独抽出方法是为了挂 UnconditionalSuppressMessage：
/// MVC 的 AddRazorPages 带 RequiresUnreferencedCode（官方声明 Razor Pages 不支持裁剪/NativeAOT），
/// 该告警属于已知项，集中在此抑制，避免掩盖其它真正有意义的裁剪告警。
/// </summary>
internal static class RazorPagesRegistration
{
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "后台 Razor 页面为独立于游戏 API 的运维界面，发布时使用非 AOT/非裁剪配置；此处告警为已知项。")]
    public static IMvcBuilder AddAdminRazorPages(this IServiceCollection services)
    {
        return services.AddRazorPages(options =>
        {
            options.Conventions.AddFolderRouteModelConvention("/", model =>
            {
                foreach (var selector in model.Selectors)
                {
                    var template = selector.AttributeRouteModel?.Template;
                    if (string.IsNullOrEmpty(template))
                        continue;

                    template = "admin/" + template.TrimEnd('/');

                    selector.AttributeRouteModel!.Template = template;
                }
            });
        });
    }
}