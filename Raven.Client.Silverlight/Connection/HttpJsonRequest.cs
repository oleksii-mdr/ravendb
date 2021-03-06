//-----------------------------------------------------------------------
// <copyright file="HttpJsonRequest.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Browser;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Raven.Abstractions.Data;
using Raven.Client.Connection;
using Raven.Client.Document;
using Raven.Json.Linq;
using Raven.Client.Extensions;

namespace Raven.Client.Silverlight.Connection
{
	/// <summary>
	/// A representation of an HTTP json request to the RavenDB server
	/// Since we are using the ClientHttp stack for Silverlight, we don't need to implement
	/// caching, it is already implemented for us.
	/// Note: that the RavenDB server generates both an ETag and an Expires header to ensure proper
	/// Note: behavior from the silverlight http stack
	/// </summary>
	public class HttpJsonRequest
	{
		private readonly string url;
		private readonly DocumentConvention conventions;
		internal HttpWebRequest webRequest;
		private byte[] postedData;
		private int retries;

		private Task RecreateWebRequest(Action<HttpWebRequest> result)
		{
			retries++;
			// we now need to clone the request, since just calling GetRequest again wouldn't do anything
			var newWebRequest = (HttpWebRequest)WebRequestCreator.ClientHttp.Create(new Uri(url));
			newWebRequest.Method = webRequest.Method;
			HttpJsonRequestHelper.CopyHeaders(webRequest, newWebRequest);
			newWebRequest.Credentials = webRequest.Credentials;
			result(newWebRequest);
			webRequest = newWebRequest;

			if (postedData == null)
			{
				var taskCompletionSource = new TaskCompletionSource<object>();
				taskCompletionSource.SetResult(null);
				return taskCompletionSource.Task;
			}
			else return WriteAsync(postedData);
		}

		/// <summary>
		/// Gets or sets the response headers.
		/// </summary>
		/// <value>The response headers.</value>
		public IDictionary<string, IList<string>> ResponseHeaders { get; set; }

		internal HttpJsonRequest(string url, string method, RavenJObject metadata, DocumentConvention conventions)
		{
			this.url = url;
			this.conventions = conventions;
			webRequest = (HttpWebRequest)WebRequestCreator.ClientHttp.Create(new Uri(url));

			WriteMetadata(metadata);
			webRequest.Method = method;
			if (method != "GET")
				webRequest.ContentType = "application/json; charset=utf-8";
		}

		/// <summary>
		/// Begins the read response string.
		/// </summary>
		/// <param name="callback">The callback.</param>
		/// <param name="state">The state.</param>
		/// <returns></returns>
		public Task<string> ReadResponseStringAsync()
		{
			return webRequest
				.GetResponseAsync()
				.ContinueWith(t => ReadStringInternal(() => t.Result))
				.ContinueWith(task => RetryIfNeedTo(task, ReadResponseStringAsync))
				.Unwrap();
		}

		private Task<T> RetryIfNeedTo<T>(Task<T> task, Func<Task<T>> generator)
		{
			var exception = task.Exception.ExtractSingleInnerException() as WebException;
			if (exception == null || retries >= 3)
				return task;

			var webResponse = exception.Response as HttpWebResponse;
			if (webResponse == null || webResponse.StatusCode != HttpStatusCode.Unauthorized)
				return task;

			var authorizeResponse = HandleUnauthorizedResponseAsync(webResponse);

			if (authorizeResponse == null)
				return task; // effectively throw

			
			return authorizeResponse
				.ContinueWith(task1 =>
				{
					task1.Wait();// throw if error
					return generator();
				})
				.Unwrap();
		}

		public Task HandleUnauthorizedResponseAsync(HttpWebResponse unauthorizedResponse)
		{
			if (conventions.HandleUnauthorizedResponseAsync == null)
				return null;

			var unauthorizedResponseAsync = conventions.HandleUnauthorizedResponseAsync(unauthorizedResponse);

			if (unauthorizedResponseAsync == null)
				return null;

			return unauthorizedResponseAsync.ContinueWith(task => RecreateWebRequest(task.Result)).Unwrap();
		}

		public Task<byte[]> ReadResponseBytesAsync()
		{
			return webRequest
				.GetResponseAsync()
				.ContinueWith(t => ReadResponse(() => t.Result, ConvertStreamToBytes))
				.ContinueWith(task => RetryIfNeedTo(task, ReadResponseBytesAsync))
				.Unwrap(); ;
		}

		static byte[] ConvertStreamToBytes(Stream input)
		{
			var buffer = new byte[16 * 1024];
			using (var ms = new MemoryStream())
			{
				int read;
				while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
				{
					ms.Write(buffer, 0, read);
				}
				return ms.ToArray();
			}
		}

		private string ReadStringInternal(Func<WebResponse> getResponse)
		{
			return ReadResponse(getResponse, responseStream =>
				{
					var reader = new StreamReader(responseStream);
					var text = reader.ReadToEnd();
					return text;
				}
			);

		}

		private T ReadResponse<T>(Func<WebResponse> getResponse, Func<Stream, T> handleResponse)
		{
			WebResponse response;
			try
			{
				response = getResponse();
			}
			catch (WebException e)
			{
				var httpWebResponse = e.Response as HttpWebResponse;
				if (httpWebResponse == null ||
					httpWebResponse.StatusCode == HttpStatusCode.NotFound ||
						httpWebResponse.StatusCode == HttpStatusCode.Conflict)
					throw;

				using (var sr = new StreamReader(e.Response.GetResponseStream()))
				{
					throw new InvalidOperationException(sr.ReadToEnd(), e);
				}
			}

			ResponseHeaders = response.Headers.AllKeys
					.ToDictionary(key => key, key => (IList<string>)new List<string> { response.Headers[key] });
			ResponseStatusCode = ((HttpWebResponse)response).StatusCode;

			using (var responseStream = response.GetResponseStream())
			{
				return handleResponse(responseStream);
			}
		}


		/// <summary>
		/// Gets or sets the response status code.
		/// </summary>
		/// <value>The response status code.</value>
		public HttpStatusCode ResponseStatusCode { get; set; }

		private void WriteMetadata(RavenJObject metadata)
		{
			foreach (var prop in metadata)
			{
				if (prop.Value == null)
					continue;

				if (prop.Value.Type == JTokenType.Object ||
					prop.Value.Type == JTokenType.Array)
					continue;

				var headerName = prop.Key;
				if (headerName == "ETag")
					headerName = "If-Match";
				if(headerName.StartsWith("@") ||
					headerName == Constants.LastModified)
					continue;
				var value = prop.Value.Value<object>().ToString();
				switch (headerName)
				{
					case "Content-Length":
						break;
					case "Content-Type":
						webRequest.ContentType = value;
						break;
					default:
						webRequest.Headers[headerName] = value;
						break;
				}
			}
		}

		/// <summary>
		/// Begins the write operation
		/// </summary>
		public Task WriteAsync(string data)
		{
			var byteArray = Encoding.UTF8.GetBytes(data);
			return WriteAsync(byteArray);
		}

		/// <summary>
		/// Begins the write operation
		/// </summary>
		public Task WriteAsync(byte[] byteArray)
		{
			postedData = byteArray;
			return webRequest.GetRequestStreamAsync().ContinueWith(t =>
			{
				var dataStream = t.Result;
				using (dataStream)
				{
					dataStream.Write(byteArray, 0, byteArray.Length);
					dataStream.Close();
				}
			});
		}

		/// <summary>
		/// Adds the operation headers.
		/// </summary>
		/// <param name="operationsHeaders">The operations headers.</param>
		public HttpJsonRequest AddOperationHeaders(IDictionary<string, string> operationsHeaders)
		{
			foreach (var header in operationsHeaders)
			{
				webRequest.Headers[header.Key] = header.Value;
			}
			return this;
		}

		/// <summary>
		/// Adds the operation header
		/// </summary>
		public HttpJsonRequest AddOperationHeader(string key, string value)
		{
			webRequest.Headers[key] = value;
			return this;
		}
	}
}