namespace CloudHttp.Tests;

public static class HttpRouteBuilderTests
{
    public class BuildPathTests
    {
        [Fact]
        public void Substitutes_single_placeholder()
        {
            var path = HttpRouteBuilder.BuildPath("/api/users/{id}", new Dictionary<string, object?>
            {
                ["id"] = 42,
            });

            path.Should().Be("/api/users/42");
        }

        [Fact]
        public void Substitutes_multiple_placeholders()
        {
            var path = HttpRouteBuilder.BuildPath("/api/v{ver}/users/{id}/posts/{postId}", new Dictionary<string, object?>
            {
                ["ver"] = 2,
                ["id"] = "abc",
                ["postId"] = 7,
            });

            path.Should().Be("/api/v2/users/abc/posts/7");
        }

        [Fact]
        public void Escapes_values()
        {
            var path = HttpRouteBuilder.BuildPath("/users/{name}", new Dictionary<string, object?>
            {
                ["name"] = "hello world&foo",
            });

            path.Should().Be("/users/hello%20world%26foo");
        }

        [Fact]
        public void Null_value_becomes_empty_string()
        {
            var path = HttpRouteBuilder.BuildPath("/users/{id}", new Dictionary<string, object?>
            {
                ["id"] = null,
            });

            path.Should().Be("/users/");
        }

        [Fact]
        public void Unmatched_placeholder_left_in_place()
        {
            var path = HttpRouteBuilder.BuildPath("/users/{id}/{name}", new Dictionary<string, object?>
            {
                ["id"] = 1,
            });

            path.Should().Be("/users/1/{name}");
        }

        [Fact]
        public void Throws_on_null_template()
        {
            var act = () => HttpRouteBuilder.BuildPath(null!, new Dictionary<string, object?>());
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Throws_on_null_parameters()
        {
            var act = () => HttpRouteBuilder.BuildPath("/x", null!);
            act.Should().Throw<ArgumentNullException>();
        }
    }

    public class AddQueryTests
    {
        [Fact]
        public void Appends_to_uri_with_no_query()
        {
            var baseUri = new Uri("https://example.com/path");

            var result = baseUri.AddQuery([
                new KeyValuePair<string, string?>("a", "1"),
                new KeyValuePair<string, string?>("b", "2")
            ]);

            result.AbsoluteUri.Should().Be("https://example.com/path?a=1&b=2");
        }

        [Fact]
        public void Appends_to_uri_with_existing_query()
        {
            var baseUri = new Uri("https://example.com/path?x=1");

            var result = baseUri.AddQuery([
                new KeyValuePair<string, string?>("y", "2")
            ]);

            result.AbsoluteUri.Should().Be("https://example.com/path?x=1&y=2");
        }

        [Fact]
        public void Escapes_keys_and_values()
        {
            var baseUri = new Uri("https://example.com/path");

            var result = baseUri.AddQuery([
                new KeyValuePair<string, string?>("key with space", "v&v")
            ]);

            result.AbsoluteUri.Should().Be("https://example.com/path?key%20with%20space=v%26v");
        }

        [Fact]
        public void Skips_nulls_by_default()
        {
            var baseUri = new Uri("https://example.com/path");

            var result = baseUri.AddQuery([
                new KeyValuePair<string, string?>("a", "1"),
                new KeyValuePair<string, string?>("b", null),
                new KeyValuePair<string, string?>("c", ""),
                new KeyValuePair<string, string?>("d", "4")
            ]);

            result.AbsoluteUri.Should().Be("https://example.com/path?a=1&d=4");
        }

        [Fact]
        public void Includes_nulls_when_skipNulls_false()
        {
            var baseUri = new Uri("https://example.com/path");

            var result = baseUri.AddQuery([
                new KeyValuePair<string, string?>("a", null),
                new KeyValuePair<string, string?>("b", "")
            ], skipNulls: false);

            result.AbsoluteUri.Should().Be("https://example.com/path?a=&b=");
        }

        [Fact]
        public void Preserves_fragment()
        {
            var baseUri = new Uri("https://example.com/path#section");

            var result = baseUri.AddQuery([
                new KeyValuePair<string, string?>("a", "1")
            ]);

            result.AbsoluteUri.Should().Be("https://example.com/path?a=1#section");
        }

        [Fact]
        public void No_params_returns_uri_without_extra_question_mark()
        {
            var baseUri = new Uri("https://example.com/path");

            var result = baseUri.AddQuery([]);

            result.AbsoluteUri.Should().Be("https://example.com/path");
        }

        [Fact]
        public void Throws_on_null_uri()
        {
            Uri? uri = null;

            var act = () => uri!.AddQuery([]);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Throws_on_null_parameters()
        {
            var baseUri = new Uri("https://example.com/");

            var act = () => baseUri.AddQuery(null!);

            act.Should().Throw<ArgumentNullException>();
        }
    }

}
