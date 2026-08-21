using System.Threading.Tasks;

namespace MissionPlannerAvalonia.Services;

internal static class SshHostKeyPrompt {
  public static Task<bool> ConfirmAsync(SshHostKeyChallenge challenge) {
    string identity = $"{challenge.Algorithm} {challenge.KeyLength}-bit\n" +
        challenge.PresentedFingerprint;
    if (!challenge.IsChanged) {
      return Dialogs.Confirm(
          "Trust SSH host key?",
          $"The identity of {challenge.Host}:{challenge.Port} is not known.\n\n" +
          identity + "\n\nTrust this key and connect?");
    }

    return Dialogs.ConfirmDangerous(
        "SSH host key changed",
        $"WARNING: the host key for {challenge.Host}:{challenge.Port} changed. " +
        "This can indicate a man-in-the-middle attack or a reinstalled companion computer.\n\n" +
        $"Trusted: {challenge.ExpectedFingerprint}\nPresented: {identity}\n\n" +
        "Replace the trusted key and reconnect?",
        "Replace key");
  }
}
