using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ART.ADK;
using ART.ADK.CRDT;
using Newtonsoft.Json.Linq;

namespace ART.ADK.Tests
{
    public class ArtAdkTests
    {
        // ---- EventEmitter Tests ----
        [Test]
        public void EventEmitter_On_Off_Emit()
        {
            var emitter = new EventEmitter();
            int callCount = 0;
            var id = emitter.On("test", data => callCount++);

            emitter.Emit("test");
            Assert.AreEqual(1, callCount);

            emitter.Emit("test");
            Assert.AreEqual(2, callCount);

            emitter.Off("test", id);
            emitter.Emit("test");
            Assert.AreEqual(2, callCount); // no more calls
        }

        [Test]
        public void EventEmitter_ListenerCount()
        {
            var emitter = new EventEmitter();
            Assert.AreEqual(0, emitter.ListenerCount("foo"));

            emitter.On("foo", _ => { });
            emitter.On("foo", _ => { });
            Assert.AreEqual(2, emitter.ListenerCount("foo"));
        }

        [Test]
        public void EventEmitter_RemoveAll()
        {
            var emitter = new EventEmitter();
            emitter.On("a", _ => { });
            emitter.On("b", _ => { });
            emitter.RemoveAllListeners();
            Assert.AreEqual(0, emitter.ListenerCount("a"));
            Assert.AreEqual(0, emitter.ListenerCount("b"));
        }

        // ---- Model Tests ----
        [Test]
        public void ConnectionDetail_Properties()
        {
            var cd = new ConnectionDetail
            {
                ConnectionId = "c1",
                InstanceId = "i1",
                TenantName = "t",
                Environment = "dev",
                ProjectKey = "pk"
            };
            Assert.AreEqual("c1", cd.ConnectionId);
            Assert.AreEqual("i1", cd.InstanceId);
        }

        [Test]
        public void PushOptions_DefaultEmpty()
        {
            var po = new PushOptions();
            Assert.IsNotNull(po.To);
            Assert.AreEqual(0, po.To.Count);
        }

        [Test]
        public void PushOptions_WithTargets()
        {
            var po = new PushOptions("user1", "user2");
            Assert.AreEqual(2, po.To.Count);
            Assert.AreEqual("user1", po.To[0]);
        }

        [Test]
        public void ChannelConfig_Defaults()
        {
            var cc = new ChannelConfig();
            Assert.AreEqual("default", cc.ChannelType);
            Assert.AreEqual("", cc.ChannelNamespace);
        }

        // ---- ARTException Tests ----
        [Test]
        public void ARTExceptions_HaveCorrectMessages()
        {
            Assert.IsTrue(new ARTForbiddenException("denied").Message.Contains("Forbidden"));
            Assert.IsTrue(new ARTAuthenticationException("bad").Message.Contains("Auth"));
            Assert.IsTrue(new ARTNotConnectedException().Message.Contains("Not connected"));
            Assert.IsTrue(new ARTChannelNotFoundException("ch").Message.Contains("ch"));
            Assert.IsTrue(new ARTTimeoutException("timeout").Message.Contains("Timeout"));
        }

        // ---- CRDT Tests ----
        [Test]
        public void CRDT_GenerateId_NotEmpty()
        {
            var id = CRDTUtils.GenerateId();
            Assert.IsFalse(string.IsNullOrEmpty(id));
            Assert.IsTrue(id.Contains("-"));
        }

        [Test]
        public void CRDT_ToLDValue_Primitives()
        {
            Assert.AreEqual(LDValueType.String, CRDTUtils.ToLDValue("hello").Type);
            Assert.AreEqual(LDValueType.Number, CRDTUtils.ToLDValue(42).Type);
            Assert.AreEqual(LDValueType.Boolean, CRDTUtils.ToLDValue(true).Type);
            Assert.AreEqual(LDValueType.Null, CRDTUtils.ToLDValue(null).Type);
        }

        [Test]
        public void CRDT_ToLDValue_Dictionary()
        {
            var val = CRDTUtils.ToLDValue(new JObject { ["name"] = "test" });
            Assert.AreEqual(LDValueType.Map, val.Type);
            Assert.IsTrue(val.MapValue.Index.ContainsKey("name"));
        }

        [Test]
        public void CRDT_ToAny_Roundtrip()
        {
            var original = "hello";
            var ldVal = CRDTUtils.ToLDValue(original);
            var result = CRDTUtils.ToAny(ldVal);
            Assert.AreEqual(original, result);
        }

        [Test]
        public void CRDT_Engine_SetAndRead()
        {
            var engine = new CRDTEngine(new LDMap(), ops => { });
            engine.SetReplicaId("test-client");

            var proxy = engine.State();
            proxy["name"].Set("Alice");

            // Flush synchronously for test
            engine.Flush().Wait();

            var value = proxy["name"].Value;
            Assert.AreEqual("Alice", value);
        }

        [Test]
        public void CRDT_Engine_Delete()
        {
            var engine = new CRDTEngine(new LDMap(), ops => { });
            engine.SetReplicaId("test-client");

            var proxy = engine.State();
            proxy["temp"].Set("data");
            engine.Flush().Wait();
            Assert.IsNotNull(proxy["temp"].Value);

            proxy["temp"].Delete();
            engine.Flush().Wait();
            Assert.IsNull(proxy["temp"].Value);
        }

        [Test]
        public void CRDT_LinearizeRGA_Empty()
        {
            var arr = new LDArray();
            var ids = CRDTUtils.LinearizeRGA(arr);
            Assert.AreEqual(0, ids.Count);
        }

        // ---- Encryption Tests ----
        [Test]
        public void EncryptionHelper_GenerateKeyPair()
        {
            var kp = EncryptionHelper.GenerateKeyPair();
            Assert.IsFalse(string.IsNullOrEmpty(kp.PublicKey));
            Assert.IsFalse(string.IsNullOrEmpty(kp.PrivateKey));
            Assert.AreNotEqual(kp.PublicKey, kp.PrivateKey);
        }

        [Test]
        public void EncryptionHelper_EncryptDecrypt_Roundtrip()
        {
            var alice = EncryptionHelper.GenerateKeyPair();
            var bob = EncryptionHelper.GenerateKeyPair();
            var original = "Hello, Bob! This is a secret message.";

            // Alice encrypts for Bob
            var encrypted = EncryptionHelper.Encrypt(original, bob.PublicKey, alice.PrivateKey);
            Assert.AreNotEqual(original, encrypted);

            // Bob decrypts from Alice
            var decrypted = EncryptionHelper.Decrypt(encrypted, alice.PublicKey, bob.PrivateKey);
            Assert.AreEqual(original, decrypted);
        }

        [Test]
        public void EncryptionHelper_TamperDetection()
        {
            var alice = EncryptionHelper.GenerateKeyPair();
            var bob = EncryptionHelper.GenerateKeyPair();
            var original = "Secret";

            var encryptedB64 = EncryptionHelper.Encrypt(original, bob.PublicKey, alice.PrivateKey);
            var encryptedBytes = System.Convert.FromBase64String(encryptedB64);

            // Tamper with the ciphertext (the last byte)
            encryptedBytes[encryptedBytes.Length - 1] ^= 0xFF;
            var tamperedB64 = System.Convert.ToBase64String(encryptedBytes);

            Assert.Throws<ARTDecryptionException>(() =>
            {
                EncryptionHelper.Decrypt(tamperedB64, alice.PublicKey, bob.PrivateKey);
            });
        }

        // ---- Adk Config Tests ----
        [Test]
        public void AdkConfig_CanSetCredentials()
        {
            var config = new AdkConfig
            {
                Uri = "ws.test.com",
                GetCredentials = () => new CredentialStore
                {
                    Environment = "dev",
                    ProjectKey = "pk",
                    OrgTitle = "org",
                    ClientID = "cid",
                    ClientSecret = "cs"
                }
            };

            Assert.AreEqual("ws.test.com", config.Uri);
            var creds = config.GetCredentials();
            Assert.AreEqual("dev", creds.Environment);
            Assert.AreEqual("pk", creds.ProjectKey);
        }
    }
}
