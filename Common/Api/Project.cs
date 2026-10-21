/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/
using System;
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace QuantConnect.Api
{
    /// <summary>
    /// Collaborator responses
    /// </summary>
    public class Collaborator
    {
        /// <summary>
        /// User ID
        /// </summary>
        [JsonPropertyName("uid")]
        public int? Uid { get; set; }

        /// <summary>
        /// Indicate if the user have live control
        /// </summary>
        [JsonPropertyName("liveControl")]
        public bool LiveControl { get; set; }

        /// <summary>
        /// The permission this user is given. Can be "read"
        /// or "write"
        /// </summary>
        [JsonPropertyName("permission")]
        public string Permission { get; set; }

        /// <summary>
        /// The user public ID
        /// </summary>
        [JsonPropertyName("publicId")]
        public string PublicId { get; set; }

        /// <summary>
        /// The url of the user profile image
        /// </summary>
        [JsonPropertyName("profileImage")]
        public string ProfileImage { get; set; }

        /// <summary>
        /// The registered email of the user
        /// </summary>
        [JsonPropertyName("email")]
        public string Email { get; set; }

        /// <summary>
        /// The display name of the user
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// The biography of the user
        /// </summary>
        [JsonPropertyName("bio")]
        public string Bio { get; set; }

        /// <summary>
        /// Indicate if the user is the owner of the project
        /// </summary>
        [JsonPropertyName("owner")]
        public bool Owner { get; set; }
    }

    /// <summary>
    /// Library response
    /// </summary>
    public class Library
    {

        /// <summary>
        /// Project Id of the library project
        /// </summary>
        [JsonPropertyName("projectId")]
        public int Projectid { get; set; }

        /// <summary>
        /// Name of the library project
        /// </summary>
        [JsonPropertyName("libraryName")]
        public string LibraryName { get; set; }

        /// <summary>
        /// Name of the library project owner
        /// </summary>
        [JsonPropertyName("ownerName")]
        public string OwnerName { get; set; }

        /// <summary>
        /// Indicate if the library project can be accessed
        /// </summary>
        [JsonPropertyName("access")]
        public bool Access { get; set; }
    }

    /// <summary>
    /// The chart display properties
    /// </summary>
    public class GridChart
    {
        /// <summary>
        /// The chart name
        /// </summary>
        [JsonPropertyName("chartName")]
        public string ChartName { get; set; }

        /// <summary>
        /// Width of the chart
        /// </summary>
        [JsonPropertyName("width")]
        public int Width { get; set; }

        /// <summary>
        /// Height of the chart
        /// </summary>
        [JsonPropertyName("height")]
        public int Height { get; set; }

        /// <summary>
        /// Number of rows of the chart
        /// </summary>
        [JsonPropertyName("row")]
        public int Row { get; set; }

        /// <summary>
        /// Number of columns of the chart
        /// </summary>
        [JsonPropertyName("column")]
        public int Column { get; set; }

        /// <summary>
        /// Sort of the chart
        /// </summary>
        [JsonPropertyName("sort")]
        public int Sort { get; set; }

        /// <summary>
        /// Optionally related definition
        /// </summary>
        [JsonPropertyName("definition")]
        public List<string> Definition { get; set; }
    }

    /// <summary>
    /// The grid arrangement of charts
    /// </summary>
    public class Grid
    {
        /// <summary>
        /// List of chart in the xs (Extra small) position
        /// </summary>
        [JsonPropertyName("xs")]
        public List<GridChart> Xs { get; set; }

        /// <summary>
        /// List of chart in the sm (Small) position
        /// </summary>
        [JsonPropertyName("sm")]
        public List<GridChart> Sm { get; set; }

        /// <summary>
        /// List of chart in the md (Medium) position
        /// </summary>
        [JsonPropertyName("md")]
        public List<GridChart> Md { get; set; }

        /// <summary>
        /// List of chart in the lg (Large) position
        /// </summary>
        [JsonPropertyName("lg")]
        public List<GridChart> Lg { get; set; }

        /// <summary>
        /// List of chart in the xl (Extra large) position
        /// </summary>
        [JsonPropertyName("xl")]
        public List<GridChart> Xl { get; set; }
    }

    /// <summary>
    /// Encryption key details
    /// </summary>
    public class EncryptionKey
    {
        /// <summary>
        /// Encryption key id
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>
        /// Name of the encryption key
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// Parameter set
    /// </summary>
    public class Parameter
    {
        /// <summary>
        /// Name of parameter
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// Value of parameter
        /// </summary>
        [JsonPropertyName("value")]
        public string Value { get; set; }

        /// <summary>
        /// Name of parameter in the {"key": key, "value": value} format
        /// </summary>
        [JsonPropertyName("key")]
        private string Key { set { Name = value ?? Name; } }
    }

    /// <summary>
    /// Response from reading a project by id.
    /// </summary>
    public class Project : RestResponse
    {
        /// <summary>
        /// Project id
        /// </summary>
        [JsonPropertyName("projectId")]
        public int ProjectId { get; set; }

        /// <summary>
        /// Name of the project
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// Date the project was created
        /// </summary>
        [JsonPropertyName("created")]
        public DateTime Created { get; set; }

        /// <summary>
        /// Modified date for the project
        /// </summary>
        [JsonPropertyName("modified")]
        public DateTime Modified { get; set; }

        /// <summary>
        /// Programming language of the project
        /// </summary>
        [JsonPropertyName("language")]
        public Language Language { get; set; }

        /// <summary>
        /// The projects owner id
        /// </summary>
        [JsonPropertyName("ownerId")]
        public int OwnerId { get; set; }

        /// <summary>
        /// The organization ID
        /// </summary>
        [JsonPropertyName("organizationId")]
        public string OrganizationId { get; set; }

        /// <summary>
        /// List of collaborators
        /// </summary>
        [JsonPropertyName("collaborators")]
        public List<Collaborator> Collaborators { get; set; }

        /// <summary>
        /// The version of LEAN this project is running on
        /// </summary>
        [JsonPropertyName("leanVersionId")]
        public int LeanVersionId { get; set; }

        /// <summary>
        /// Indicate if the project is pinned to the master branch of LEAN
        /// </summary>
        [JsonPropertyName("leanPinnedToMaster")]
        public bool LeanPinnedToMaster { get; set; }

        /// <summary>
        /// Indicate if you are the owner of the project
        /// </summary>
        [JsonPropertyName("owner")]
        public bool Owner { get; set; }

        /// <summary>
        /// The project description
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; }

        /// <summary>
        /// Channel id
        /// </summary>
        [JsonPropertyName("channelId")]
        public string ChannelId { get; set; }

        /// <summary>
        /// Optimization parameters
        /// </summary>
        [JsonPropertyName("parameters")]
        public List<Parameter> Parameters { get; set; }

        /// <summary>
        /// The library projects
        /// </summary>
        [JsonPropertyName("libraries")]
        public List<Library> Libraries { get; set; }

        /// <summary>
        /// Configuration of the backtest view grid
        /// </summary>
        [JsonPropertyName("grid")]
        public Grid Grid { get; set; }

        /// <summary>
        /// Configuration of the live view grid
        /// </summary>
        [JsonPropertyName("liveGrid")]
        public Grid LiveGrid { get; set; }

        /// <summary>
        /// The equity value of the last paper trading instance
        /// </summary>
        [JsonPropertyName("paperEquity")]
        public decimal? PaperEquity { get; set; }

        /// <summary>
        /// The last live deployment active time
        /// </summary>
        [JsonPropertyName("lastLiveDeployment")]
        public DateTime? LastLiveDeployment { get; set; }

        /// <summary>
        /// The last live wizard content used
        /// </summary>
        [JsonPropertyName("liveForm")]
        public object LiveForm { get; set; }

        /// <summary>
        /// Indicates if the project is encrypted
        /// </summary>
        [JsonPropertyName("encrypted")]
        public bool? Encrypted { get; set; }

        /// <summary>
        /// Indicates if the project is running or not
        /// </summary>
        [JsonPropertyName("codeRunning")]
        public bool CodeRunning { get; set; }

        /// <summary>
        /// LEAN environment of the project running on
        /// </summary>
        [JsonPropertyName("leanEnvironment")]
        public int LeanEnvironment { get; set; }

        /// <summary>
        /// Text file with at least 32 characters to be used to encrypt the project
        /// </summary>
        [JsonPropertyName("encryptionKey")]
        public EncryptionKey EncryptionKey { get; set; }

        /// <summary>
        /// The maximum file length allowed in the project
        /// </summary>
        [JsonPropertyName("maxFileSize")]
        public int MaxFileSize { get; set; }
    }

    /// <summary>
    /// API response for version
    /// </summary>
    public class Version
    {
        /// <summary>
        /// ID of the LEAN version
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Date when this version was created
        /// </summary>
        [JsonPropertyName("created")]
        public DateTime? Created { get; set; }

        /// <summary>
        /// Description of the LEAN version
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; }

        /// <summary>
        /// Commit Hash in the LEAN repository
        /// </summary>
        [JsonPropertyName("leanHash")]
        public string LeanHash { get; set; }

        /// <summary>
        /// Commit Hash in the LEAN Cloud repository
        /// </summary>
        [JsonPropertyName("leanCloudHash")]
        public string LeanCloudHash { get; set; }

        /// <summary>
        /// Name of the branch where the commit is
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// Reference to the branch where the commit is
        /// </summary>
        [JsonPropertyName("ref")]
        public string Ref { get; set; }

        /// <summary>
        /// Indicates if the version is available for the public (1) or not (0)
        /// </summary>
        [JsonPropertyName("public")]
        public bool Public { get; set; }
    }

    /// <summary>
    /// Read versions response
    /// </summary>
    public class VersionsResponse : RestResponse
    {
        /// <summary>
        /// List of LEAN versions
        /// </summary>
        [JsonPropertyName("versions")]
        public List<Version> Versions { get; set; }
    }

    /// <summary>
    /// Project list response
    /// </summary>
    public class ProjectResponse : VersionsResponse
    {
        /// <summary>
        /// List of projects for the authenticated user
        /// </summary>
        [JsonPropertyName("projects")]
        public List<Project> Projects { get; set; }
    }
}
