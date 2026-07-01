using System.Diagnostics;
using Dapper;

namespace Javideo.Worker.Services;

/// <summary>
/// Launches a media file in the user's configured player (from settings), or
/// falls back to the OS default opener when no player is configured.
/// </summary>
public sealed class PlayerService
{
    private readonly SettingsService _settings;
    public PlayerService(SettingsService settings) => _settings = settings;

    public record PlayResult(bool Ok, string Detail);

    public async Task<PlayResult> PlayAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return new PlayResult(false, "未提供文件路径");
        if (!File.Exists(filePath))
            return new PlayResult(false, $"文件不存在: {filePath}");

        try
        {
            var playerPath = (await _settings.GetAsync(SettingsService.KeyPlayerPath))?.Trim();

            if (!string.IsNullOrWhiteSpace(playerPath) && File.Exists(playerPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = playerPath,
                    ArgumentList = { filePath },
                    UseShellExecute = false,
                });
                return new PlayResult(true, $"已用 {Path.GetFileName(playerPath)} 打开");
            }

            // Fallback: OS default handler for the file type.
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true,
            });
            return new PlayResult(true, "已用系统默认程序打开(未配置播放器)");
        }
        catch (Exception ex)
        {
            return new PlayResult(false, ex.Message);
        }
    }
}
