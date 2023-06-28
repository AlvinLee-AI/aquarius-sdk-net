﻿using System;
using Aquarius.Helpers;
using Aquarius.TimeSeries.Client.Helpers;
using FluentAssertions;
using NUnit.Framework;
using ServiceStack;

#if AUTOFIXTURE4
using AutoFixture;
#else
using Ploeh.AutoFixture;
#endif

namespace Aquarius.Client.UnitTests.TimeSeries.Client.Helpers
{
    [TestFixture]
    public class ClientHelperTests
    {
        private JsonServiceClient _rawClient;
        private IFixture _fixture;

        [SetUp]
        public void ForEachTest()
        {
            _rawClient = new SdkServiceClient();
            _fixture = new Fixture();
        }

        [Test]
        public void SetAuthenticationToken_NoExistingToken_NewTokenIsAdded()
        {
            AssertThatNoTokenExists();

            var newToken = _fixture.Create<string>();

            SetAuthenticationToken(newToken);

            AssertThatTokenMatchesExpected(newToken);
        }

        private void SetAuthenticationToken(string token)
        {
            ClientHelper.SetAuthenticationToken(_rawClient, token);
        }

        private void AssertThatNoTokenExists()
        {
            Assert.That(_rawClient.Headers[AuthenticationHeaders.AuthenticationHeaderNameKey], Is.Null);
        }

        private void AssertThatTokenMatchesExpected(string expectedToken)
        {
            _rawClient.Headers[AuthenticationHeaders.AuthenticationHeaderNameKey].ShouldBeEquivalentTo(expectedToken);
        }

        [Test]
        public void SetAuthenticationToken_ExistingToken_NewTokenIsAdded()
        {
            var token1 = _fixture.Create<string>();
            var token2 = _fixture.Create<string>();

            Assert.That(token1, Is.Not.EqualTo(token2));

            SetAuthenticationToken(token1);

            AssertThatTokenMatchesExpected(token1);

            SetAuthenticationToken(token2);

            AssertThatTokenMatchesExpected(token2);
        }

        [Test]
        public void Login_IdentityProvider_SetsBearerTokenAsAccessToken()
        {
            var mockAccessToken = _fixture.Create<string>();
            ClientHelper.Login(_rawClient, mockAccessToken);

            _rawClient.BearerToken.ShouldBeEquivalentTo(mockAccessToken);
        }

        [TestCase(null)]
        [TestCase("FooBar")]
        public void Login_ExistingAccessToken_NewAccessToken_UpdatesBearerToken(string updatedAccessToken)
        {
            var mockAccessToken = _fixture.Create<string>();
            ClientHelper.Login(_rawClient, mockAccessToken);
            _rawClient.BearerToken.ShouldBeEquivalentTo(mockAccessToken);

            ClientHelper.Login(_rawClient, updatedAccessToken);
            _rawClient.BearerToken.ShouldBeEquivalentTo(updatedAccessToken);
        }

        [Test]
        public void CloneAuthenticatedClient_WithNullClient_Throws()
        {
            Action action = () => ClientHelper.CloneAuthenticatedClient(null, string.Empty);

            action.ShouldThrow<ArgumentNullException>();
        }

        [Test]
        public void CloneAuthenticatedClient_WithValidClient_ClonesAuthenticationToken()
        {
            var token = _fixture.Create<string>();

            SetAuthenticationToken(token);
            ClientHelper.Login(_rawClient, token);

            var host = "somehost";
            var port = 1234;
            var scheme = Uri.UriSchemeHttps;
            var path = "/some/path";

            var builder = new UriBuilder(scheme, host, port, path);

            _rawClient.SetBaseUri(builder.ToString());

            var baseUri = "/a/different/path";
            Assert.That(path, Is.Not.EqualTo(baseUri), "Invalid test data");

            var clone = ClientHelper.CloneAuthenticatedClient(_rawClient, baseUri);

            clone.Headers[AuthenticationHeaders.AuthenticationHeaderNameKey].ShouldBeEquivalentTo(_rawClient.Headers[AuthenticationHeaders.AuthenticationHeaderNameKey]);

            var uri = new Uri(clone.BaseUri);

            uri.Host.ShouldBeEquivalentTo(host, "Original host retained");
            uri.Port.ShouldBeEquivalentTo(port, "Original port retained");
            uri.Scheme.ShouldBeEquivalentTo(scheme, "Original scheme retained");
            uri.PathAndQuery.ShouldBeEquivalentTo(baseUri, "New endpoint");
            clone.ShouldBeEquivalentTo(_rawClient, options => options
                .Excluding(client => client.StreamDeserializer)
                .Excluding(client => client.BaseUri)
                .Excluding(client => client.SyncReplyBaseUri)
                .Excluding(client => client.AsyncOneWayBaseUri)
            );
        }
    }
}
