using Microsoft.Extensions.Logging;
using Protocol;
using Google.Protobuf;
using XiaomiAstroBoxCSharp.Protocol;
using System.Threading;
using System;
using System.Threading.Tasks;
using System.Linq;

namespace XiaomiAstroBoxCSharp.Authentication;

public class AuthenticationHandler(ILogger<AuthenticationHandler> _logger, string authKey, Func<WearPacket, bool, CancellationToken, Task> sendPacketFunc)
{

    // Authentication state
    private byte[] _randomBytes = [];
    private byte[] _sendingDataEncryptionKey = [];
    private byte[] _receivingDataDecryptionKey = [];
    // nonces to prevent replay attacks
    // nonce stands for "number used once"
    private byte[] _encNonce = [];
    private byte[] _decNonce = [];
    private TaskCompletionSource<bool> _authTcs;
    private CancellationToken _authCt = default;

    public bool IsAuthenticated { get; private set; }
    public L2Cipher Cipher { get; private set; }

    public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        if (IsAuthenticated)
        {
            _logger.LogDebug("Already authenticated, skipping");
            return true;
        }

        _logger.LogInformation("Starting authentication...");

        _authTcs = new TaskCompletionSource<bool>();
        _authCt = ct;

        // Generate random nonce and send AuthAppVerify
        _randomBytes = CryptographyHelper.GenerateRandomBytes(16);
        _logger.LogDebug("Generated app random bytes: {RandomBytes}", BitConverter.ToString(_randomBytes));

        var authVerify = new Auth.Types.AppVerify
        {
            AppRandom = ByteString.CopyFrom(_randomBytes)
        };

        var account = new Account
        {
            AuthAppVerify = authVerify
        };

        var packet = new WearPacket
        {
            Type = WearPacket.Types.Type.Account,
            Id = (uint)Account.Types.AccountID.AuthVerify,
            Account = account
        };

        _logger.LogInformation("Sending AuthAppVerify packet...");
        await sendPacketFunc(packet, false, ct);

        // Wait for authentication to complete (with timeout)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            IsAuthenticated = await _authTcs.Task.WaitAsync(timeoutCts.Token);

            if (IsAuthenticated)
            {
                _logger.LogInformation("Authentication successful!");
                _logger.LogDebug("Encryption key: {EncKey}", BitConverter.ToString(_sendingDataEncryptionKey));
                _logger.LogDebug("Decryption key: {DecKey}", BitConverter.ToString(_receivingDataDecryptionKey));
                _logger.LogDebug("Encryption nonce: {EncNonce}", BitConverter.ToString(_encNonce));
                _logger.LogDebug("Decryption nonce: {DecNonce}", BitConverter.ToString(_decNonce));

                // Create cipher for encrypted communication
                Cipher = new L2Cipher(_sendingDataEncryptionKey, _receivingDataDecryptionKey, _logger);
            }
            else
            {
                _logger.LogError("Authentication failed");
            }

            return IsAuthenticated;
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("Authentication timeout after 30 seconds");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during authentication");
            return false;
        }
    }

    public async Task ProcessAuthPacketAsync(WearPacket packet)
    {
        if (packet.PayloadCase != WearPacket.PayloadOneofCase.Account)
            return;

        var account = packet.Account;

        switch (account.PayloadCase)
        {
            case Account.PayloadOneofCase.AuthDeviceVerify:
                _logger.LogInformation("Received AuthDeviceVerify");
                await ProcessAuthStep2Async(account.AuthDeviceVerify);
                break;

            case Account.PayloadOneofCase.AuthDeviceConfirm:
                var confirm = account.AuthDeviceConfirm;
                _logger.LogInformation("Received AuthDeviceConfirm - Result: {Result}",
                    confirm.ConfirmResult);
                _authTcs?.TrySetResult(confirm.ConfirmResult);
                break;
        }
    }

    private async Task ProcessAuthStep2Async(Auth.Types.DeviceVerify deviceVerify)
    {
        try
        {
            var watchRandom = deviceVerify.DeviceRandom.ToByteArray();
            var watchSign = deviceVerify.DeviceSign.ToByteArray();

            _logger.LogDebug("Device random: {Random}", BitConverter.ToString(watchRandom));
            _logger.LogDebug("Device sign: {Sign}", BitConverter.ToString(watchSign));

            if (watchRandom.Length != 16 || watchSign.Length != 32)
            {
                _logger.LogError("Invalid nonce/HMAC length: random={RandomLen}, sign={SignLen}", 
                    watchRandom.Length, watchSign.Length);
                throw new InvalidOperationException("Invalid nonce/HMAC length");
            }

            _logger.LogDebug("Using auth key: {Key}", authKey);
            var authKeyBytes = CryptographyHelper.StringToBytes16(authKey);
            if (authKeyBytes == null)
            {
                _logger.LogError("Failed to parse auth key");
                throw new InvalidOperationException("Invalid auth key format");
            }

            // Derive keys using KDF
            _logger.LogDebug("Running KDF with authKey, appRandom, deviceRandom");
            // a KDF is a key derivation function
            var block64 = CryptographyHelper.KdfMiWear(authKeyBytes, _randomBytes, watchRandom);
            _logger.LogDebug("KDF output (64 bytes): {Output}", BitConverter.ToString(block64));

            // these are the random byte sequences that the watch uses to send encrypted packets
            _receivingDataDecryptionKey = [.. block64[0..16]];
            _sendingDataEncryptionKey = [.. block64[16..32]];
            _decNonce = [.. block64[32..36]];
            _encNonce = [.. block64[36..40]];

            _logger.LogDebug("Derived decryption key: {DecKey}", BitConverter.ToString(_receivingDataDecryptionKey));
            _logger.LogDebug("Derived encryption key: {EncKey}", BitConverter.ToString(_sendingDataEncryptionKey));

            // Verify HMAC
            _logger.LogDebug("Computing expected HMAC");
            var expectedHmac = CryptographyHelper.HmacSha256(_receivingDataDecryptionKey, watchRandom, _randomBytes);
            _logger.LogDebug("Expected HMAC: {Expected}", BitConverter.ToString(expectedHmac));
            _logger.LogDebug("Received HMAC: {Received}", BitConverter.ToString(watchSign));
            
            if (!watchSign.SequenceEqual(expectedHmac))
            {
                _logger.LogError("HMAC verification failed! Auth key is incorrect.");
                throw new InvalidOperationException("Auth HMAC mismatch - invalid auth key");
            }

            _logger.LogInformation("HMAC verified successfully");

            // Create encrypted signature
            _logger.LogDebug("Computing app signature HMAC");
            var appSign = CryptographyHelper.HmacSha256(_sendingDataEncryptionKey, _randomBytes, watchRandom);
            _logger.LogDebug("App signature: {Sign}", BitConverter.ToString(appSign));

            // Create companion device info
            _logger.LogDebug("Creating companion device info");
            var companionDevice = new CompanionDevice
            {
                DeviceType = CompanionDevice.Types.DeviceType.Android,
                DeviceName = "AstroBox",
                AppCapability = 0xffffffff
            };

            // Encrypt companion device info
            var companionBytes = companionDevice.ToByteArray();
            _logger.LogDebug("Companion device protobuf ({Length} bytes): {Data}", 
                companionBytes.Length, BitConverter.ToString(companionBytes));
            
            var nonce12 = new byte[12];
            Array.Copy(_encNonce, 0, nonce12, 0, 4);
            _logger.LogDebug("CCM nonce: {Nonce}", BitConverter.ToString(nonce12));
            // Rest is zeros (counter)

            _logger.LogDebug("Encrypting companion device with AES-128-CCM");
            var encryptedDevice = CryptographyHelper.Aes128CcmEncrypt(_sendingDataEncryptionKey, nonce12, Array.Empty<byte>(), companionBytes);
            _logger.LogDebug("Encrypted device ({Length} bytes): {Data}", 
                encryptedDevice.Length, BitConverter.ToString(encryptedDevice));

            // Send AuthAppConfirm
            _logger.LogDebug("Building AuthAppConfirm message");
            var appConfirm = new Auth.Types.AppConfirm
            {
                AppSign = ByteString.CopyFrom(appSign),
                EncryptCompanionDevice = ByteString.CopyFrom(encryptedDevice)
            };

            var account = new Account
            {
                AuthAppConfirm = appConfirm
            };

            var confirmPacket = new WearPacket
            {
                Type = WearPacket.Types.Type.Account,
                Id = (uint)Account.Types.AccountID.AuthConfirm,
                Account = account
            };

            _logger.LogInformation("Sending AuthAppConfirm packet...");
            await sendPacketFunc(confirmPacket, false, _authCt);
            _logger.LogInformation("Sent AuthAppConfirm");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auth step 2 failed");
            _authTcs?.TrySetResult(false);
        }
    }
}
