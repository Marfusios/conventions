using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace GraphQL.Conventions.Web
{
    public class Request
    {
        static readonly IRequestDeserializer _requestDeserializer = new RequestDeserializer();

        static readonly Regex _regexSuperfluousWhitespace =
            new Regex(@"[ \t\r\n]{1,}", RegexOptions.Multiline | RegexOptions.Compiled);

        readonly string _queryId;

        readonly QueryInput _queryInput;

        readonly Exception _exception;

        public static Request New(Stream stream)
        {
            stream.Seek(0, SeekOrigin.Begin);
            var requestBody = new StreamReader(stream).ReadToEnd();
            return New(requestBody);
        }

        public static Request New(string requestBody)
        {
            try
            {
                var queryInput = _requestDeserializer.GetQueryFromRequestBody(requestBody);
                return New(queryInput);
            }
            catch (Exception ex)
            {
                return InvalidInput(ex);
            }
        }

        public static Request New(QueryInput queryInput)
        {
            return new Request(queryInput);
        }

        public static Request InvalidInput(Exception exception)
        {
            return new Request(exception);
        }

        Request()
        {
            _queryId = Guid.NewGuid().ToString();
        }

        Request(QueryInput queryInput)
            : this()
        {
            _queryInput = queryInput;
        }

        Request(Exception exception)
            : this()
        {
            _exception = exception;
        }

        public string QueryId => _queryId;

        public string QueryString => _queryInput?.QueryString ?? string.Empty;

        public Dictionary<string, object> Variables => _queryInput?.Variables;

        public string OperationName => _queryInput?.OperationName;

        public bool IsValid => _exception == null;

        public Exception Error => _exception;

        public string MinifiedQueryString => MinifyString(QueryString);

        public string MinifiedVariablesString => MinifyString(JsonConvert.SerializeObject(Variables));

        static string MinifyString(string input)
        {
            if (input == null)
            {
                return string.Empty;
            }
            return _regexSuperfluousWhitespace.Replace(input, @" ").Trim();
        }
    }
}
