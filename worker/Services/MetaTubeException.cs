namespace Javideo.Worker.Services;

/// <summary>Thrown when no MetaTube server address has been configured.</summary>
public sealed class MetaTubeNotConfiguredException : Exception
{
    public MetaTubeNotConfiguredException()
        : base("未配置 MetaTube 服务地址,请到「设置 → MetaTube」填写。") { }
}
