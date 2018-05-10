using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ACMESharp.Authorizations;
using ACMESharp.Crypto;
using ACMESharp.Testing.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace ACMESharp.IntegrationTests
{
    [Collection(nameof(AcmeOrderTests))]
    [CollectionDefinition(nameof(AcmeOrderTests))]
    [TestOrder(0_20)]
    public class AcmeWildcardNameOrderTests : AcmeOrderTests
    {
        public AcmeWildcardNameOrderTests(ITestOutputHelper output,
                StateFixture state, ClientsFixture clients, AwsFixture aws)
            : base(output, state, clients, aws,
                    state.Factory.CreateLogger(typeof(AcmeMultiNameOrderTests).FullName))
        { }

        [Fact]
        [TestOrder(0_110, "WildDns")]
        public async Task Test_Create_Order_ForWildDns()
        {
            var tctx = SetTestContext();

            var dnsNames = new[] {
                $"*.{State.RandomBytesString(5)}.{TestDnsSubdomain}",
            };
            tctx.GroupSaveObject("order_names.json", dnsNames);
            Log.LogInformation("Generated random DNS name: {0}", dnsNames);

            var order = await Clients.Acme.CreateOrderAsync(dnsNames);
            tctx.GroupSaveObject("order.json", order);

            Assert.True(order.Authorizations.First().Details.Wildcard, "Is a wildcard Order");
        }

        [Fact]
        [TestOrder(0_115, "WildDns")]
        public async Task Test_Create_OrderDuplicate_ForWildDns()
        {
            var tctx = SetTestContext();

            var oldNames = tctx.GroupLoadObject<string[]>("order_names.json");
            var oldOrder = tctx.GroupLoadObject<AcmeOrder>("order.json");

            Assert.NotNull(oldNames);
            Assert.Equal(1, oldNames.Length);
            Assert.NotNull(oldOrder);
            Assert.NotNull(oldOrder.OrderUrl);

            var newOrder = await Clients.Acme.CreateOrderAsync(oldNames);
            tctx.GroupSaveObject("order-dup.json", newOrder);

            ValidateDuplicateOrder(oldOrder, newOrder);
        }

        [Fact]
        [TestOrder(0_120, "WildDns")]
        public void Test_Decode_OrderChallengeForDns01_ForSingleHttp()
        {
            var tctx = SetTestContext();

            var oldOrder = tctx.GroupLoadObject<AcmeOrder>("order.json");

            var authzIndex = 0;
            foreach (var authz in oldOrder.Authorizations)
            {
                var chlngIndex = 0;
                foreach (var chlng in authz.Details.Challenges.Where(
                    x => x.Type == Dns01ChallengeValidationDetails.Dns01ChallengeType))
                {
                    Log.LogInformation("Decoding Authorization {0} Challenge {1}",
                            authzIndex, chlngIndex);
                    
                    var chlngDetails = AuthorizationDecoder.ResolveChallengeForDns01(
                            authz, chlng, Clients.Acme.Signer);

                    Assert.Equal(Dns01ChallengeValidationDetails.Dns01ChallengeType,
                            chlngDetails.ChallengeType, ignoreCase: true);
                    Assert.NotNull(chlngDetails.DnsRecordName);
                    Assert.NotNull(chlngDetails.DnsRecordValue);
                    Assert.Equal("TXT", chlngDetails.DnsRecordType, ignoreCase: true);

                    tctx.GroupSaveObject($"order-authz_{authzIndex}-chlng_{chlngIndex}.json",
                            chlngDetails);
                    ++chlngIndex;
                }
                ++authzIndex;
            }
        }

        [Fact]
        [TestOrder(0_130, "WildDns")]
        public async Task Test_Create_OrderAnswerDnsRecords_ForWildDns()
        {
            var tctx = SetTestContext();

            var oldOrder = tctx.GroupLoadObject<AcmeOrder>("order.json");

            var authzIndex = 0;
            foreach (var authz in oldOrder.Authorizations)
            {
                var chlngIndex = 0;
                foreach (var chlng in authz.Details.Challenges.Where(
                    x => x.Type == Dns01ChallengeValidationDetails.Dns01ChallengeType))
                {
                    var chlngDetails = tctx.GroupLoadObject<Dns01ChallengeValidationDetails>(
                            $"order-authz_{authzIndex}-chlng_{chlngIndex}.json");

                    Log.LogInformation("Creating DNS for Authorization {0} Challenge {1} as per {@Details}",
                            authzIndex, chlngIndex, chlngDetails);
                    await Aws.R53.EditTxtRecord(
                            chlngDetails.DnsRecordName,
                            new[] { chlngDetails.DnsRecordValue });

                    ++chlngIndex;
                }
                ++authzIndex;
            }
        }

        [Fact]
        [TestOrder(0_135, "WildDns")]
        public async Task Test_Exist_OrderAnswerDnsRecords_ForWildDns()
        {
            var tctx = SetTestContext();

            var oldOrder = tctx.GroupLoadObject<AcmeOrder>("order.json");

            Thread.Sleep(10*1000);

            var authzIndex = 0;
            foreach (var authz in oldOrder.Authorizations)
            {
                var chlngIndex = 0;
                foreach (var chlng in authz.Details.Challenges.Where(
                    x => x.Type == Dns01ChallengeValidationDetails.Dns01ChallengeType))
                {
                    var chlngDetails = tctx.GroupLoadObject<Dns01ChallengeValidationDetails>(
                            $"order-authz_{authzIndex}-chlng_{chlngIndex}.json");

                    Log.LogInformation("Waiting on DNS record for Authorization {0} Challenge {1} as per {@Details}",
                            authzIndex, chlngIndex, chlngDetails);
 
                    var created = await ValidateDnsTxtRecord(chlngDetails.DnsRecordName,
                            targetValue: chlngDetails.DnsRecordValue);

                    Assert.True(created, "    Failed DNS set/read expected TXT record");

                    ++chlngIndex;
                }
                ++authzIndex;
            }

            // We're adding an artificial wait here -- even though we were able to successfully
            // read the expected DNS record, in practice we found it's not always "universally"
            // available from all the R53 PoP servers, specifically from where LE STAGE queries
            Thread.Sleep(10 * 1000 * oldOrder.DnsIdentifiers.Length);
        }

        [Fact]
        [TestOrder(0_140, "WildDns")]
        public async Task Test_Answer_OrderChallenges_ForWildDns()
        {
            var tctx = SetTestContext();

            var oldOrder = tctx.GroupLoadObject<AcmeOrder>("order.json");

            var authzIndex = 0;
            foreach (var authz in oldOrder.Authorizations)
            {
                var chlngIndex = 0;
                foreach (var chlng in authz.Details.Challenges.Where(
                    x => x.Type == Dns01ChallengeValidationDetails.Dns01ChallengeType))
                {
                    Log.LogInformation("Answering Authorization {0} Challenge {1}", authzIndex, chlngIndex);
                    var updated = await Clients.Acme.AnswerChallengeAsync(authz, chlng);

                    ++chlngIndex;
                }
                ++authzIndex;
            }
        }

        [Fact]
        [TestOrder(0_145, "WildDns")]
        public async Task Test_AreValid_OrderChallengesAndAuthorization_ForWildDns()
        {
            var tctx = SetTestContext();

            var oldOrder = tctx.GroupLoadObject<AcmeOrder>("order.json");

            var authzIndex = 0;
            foreach (var authz in oldOrder.Authorizations)
            {
                var chlngIndex = 0;
                foreach (var chlng in authz.Details.Challenges.Where(
                    x => x.Type == Dns01ChallengeValidationDetails.Dns01ChallengeType))
                {
                    int maxTry = 20;
                    int trySleep = 5 * 1000;
                    
                    for (var tryCount = 0; tryCount < maxTry; ++tryCount)
                    {
                        if (tryCount > 0)
                            // Wait just a bit for
                            // subsequent queries
                            Thread.Sleep(trySleep);

                        var updatedChlng = await Clients.Acme.RefreshChallengeAsync(authz, chlng);

                        // The Challenge is either Valid, still Pending or some other UNEXPECTED state

                        if ("valid" == updatedChlng.Status)
                        {
                            Log.LogInformation("    Authorization {0} Challenge {1} is VALID!", authzIndex, chlngIndex);
                            break;
                        }

                        if ("pending" != updatedChlng.Status)
                        {
                            Log.LogInformation("    Authorization {0} Challenge {1} in **UNEXPECTED STATUS**: {@UpdateChallengeDetails}",
                                    authzIndex, chlngIndex, updatedChlng);
                            throw new InvalidOperationException("Unexpected status for answered Challenge: " + updatedChlng.Status);
                        }
                    }
                    ++chlngIndex;
                }
                ++authzIndex;

                var updatedAuthz = await Clients.Acme.RefreshAuthorizationAsync(authz);
                Assert.Equal("valid", updatedAuthz.Details.Status);
            }
        }

        // [Fact]
        // [TestOrder(0_150, "WildDns")]
        // public async Task TestValidStatusForSingleNameOrder()
        // {
        //     var tctx = SetTestContext();

        //     // TODO: Validate overall order status is "valid"

        //     // This state is expected based on the ACME spec
        //     // BUT -- LE's implementation does not appear to
        //     // respect this contract -- the status of the
        //     // Order stays in the pending state even though
        //     // we are able to successfully Finalize the Order
        // }

        [Fact]
        [TestOrder(0_160, "WildDns")]
        public async Task Test_Finalize_Order_ForWildDns()
        {
            var tctx = SetTestContext();

            var oldOrder = tctx.GroupLoadObject<AcmeOrder>("order.json");

            var rsaKeys = CryptoHelper.GenerateRsaKeys(4096);
            var rsa = CryptoHelper.GenerateRsaAlgorithm(rsaKeys);
            tctx.GroupWriteTo("order-csr-keys.txt", rsaKeys);
            var derEncodedCsr = CryptoHelper.GenerateCsr(oldOrder.DnsIdentifiers, rsa);
            tctx.GroupWriteTo("order-csr.der", derEncodedCsr);

            var updatedOrder = await Clients.Acme.FinalizeOrderAsync(oldOrder, derEncodedCsr);

            int maxTry = 20;
            int trySleep = 5 * 1000;
            var valid = false;

            for (var tryCount = 0; tryCount < maxTry; ++tryCount)
            {
                if (tryCount > 0)
                {
                    // Wait just a bit for
                    // subsequent queries
                    Thread.Sleep(trySleep);

                    // Only need to refresh
                    // after the first check
                    Log.LogInformation($"  Retry #{tryCount} refreshing Order");
                    updatedOrder = await Clients.Acme.RefreshOrderAsync(oldOrder);
                    tctx.GroupSaveObject("order-updated.json", updatedOrder);
                }

                if (!valid)
                {
                    // The Order is either Valid, still Pending or some other UNEXPECTED state

                    if ("valid" == updatedOrder.Status)
                    {
                        valid = true;
                        Log.LogInformation("Order is VALID!");
                    }
                    else if ("pending" != updatedOrder.Status)
                    {
                        Log.LogInformation("Order in **UNEXPECTED STATUS**: {@UpdateChallengeDetails}", updatedOrder);
                        throw new InvalidOperationException("Unexpected status for Order: " + updatedOrder.Status);
                    }
                }

                if (valid)
                {
                    // Once it's valid, then we need to wait for the Cert
                    
                    if (!string.IsNullOrEmpty(updatedOrder.CertificateUrl))
                    {
                        Log.LogInformation("Certificate URL is ready!");
                        break;
                    }
                }
            }

            Assert.NotNull(updatedOrder.CertificateUrl);

            var certBytes = await Clients.Http.GetByteArrayAsync(updatedOrder.CertificateUrl);
            tctx.GroupWriteTo("order-cert.crt", certBytes);
        }

        [Fact]
        [TestOrder(0_170, "WildDns")]
        public async Task Test_Delete_OrderAnswerDnsRecords_ForWildDns()
        {
            var tctx = SetTestContext();

            var oldOrder = tctx.GroupLoadObject<AcmeOrder>("order.json");

            var authzIndex = 0;
            foreach (var authz in oldOrder.Authorizations)
            {
                var chlngIndex = 0;
                foreach (var chlng in authz.Details.Challenges.Where(
                    x => x.Type == Dns01ChallengeValidationDetails.Dns01ChallengeType))
                {
                    var chlngDetails = tctx.GroupLoadObject<Dns01ChallengeValidationDetails>(
                            $"order-authz_{authzIndex}-chlng_{chlngIndex}.json");

                    Log.LogInformation("Deleting DNS for Authorization {0} Challenge {1} as per {@Details}",
                            authzIndex, chlngIndex, chlngDetails);
                    await Aws.R53.EditTxtRecord(
                            chlngDetails.DnsRecordName,
                            new[] { chlngDetails.DnsRecordValue },
                            delete: true);

                    ++chlngIndex;
                }
                ++authzIndex;
            }
        }

        [Fact]
        [TestOrder(0_175, "WildDns")]
        public async Task Test_IsDeleted_OrderAnswerDnsRecords_ForMutliDns()
        {
            var tctx = SetTestContext();

            var oldOrder = tctx.GroupLoadObject<AcmeOrder>("order.json");

            Thread.Sleep(10*1000);

            var authzIndex = 0;
            foreach (var authz in oldOrder.Authorizations)
            {
                var chlngIndex = 0;
                foreach (var chlng in authz.Details.Challenges.Where(
                    x => x.Type == Dns01ChallengeValidationDetails.Dns01ChallengeType))
                {
                    var chlngDetails = tctx.GroupLoadObject<Dns01ChallengeValidationDetails>(
                            $"order-authz_{authzIndex}-chlng_{chlngIndex}.json");

                    Log.LogInformation("Waiting on DNS record deleted for Authorization {0} Challenge {1} as per {@Details}",
                            authzIndex, chlngIndex, chlngDetails);
 
                    var deleted = await ValidateDnsTxtRecord(chlngDetails.DnsRecordName,
                            targetMissing: true);

                    Assert.True(deleted, "    Failed DNS delete/read expected missing TXT record");

                    ++chlngIndex;
                }
                ++authzIndex;
            }
        }
    }
}