using Android.App;
using Android.Content;

namespace Neuroset.App.Droid;

[ContentProvider(["app.neuroset.client.files"], Exported = false, GrantUriPermissions = true)]
[MetaData("android.support.FILE_PROVIDER_PATHS", Resource = "@xml/share_paths")]
public sealed class ShareFileProvider : AndroidX.Core.Content.FileProvider;
