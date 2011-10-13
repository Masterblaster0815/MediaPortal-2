#region Copyright (C) 2007-2011 Team MediaPortal

/*
    Copyright (C) 2007-2011 Team MediaPortal
    http://www.team-mediaportal.com

    This file is part of MediaPortal 2

    MediaPortal 2 is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    MediaPortal 2 is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with MediaPortal 2. If not, see <http://www.gnu.org/licenses/>.
*/

#endregion

using System;
using System.Collections.Generic;
using System.IO;
using MediaPortal.Common.Services.MediaManagement;
using MediaPortal.Utilities.Cache;

namespace MediaPortal.Common.MediaManagement.ResourceAccess
{
  /// <summary>
  /// Access bridge logic which maps a complex resource accessor to a local file resource.
  /// </summary>
  /// <remarks>
  /// Typically, this class is instantiated by class <see cref="ResourceLocator"/> but it also can be used directly.
  /// </remarks>
  public class StreamedResourceToLocalFsAccessBridge : ILocalFsResourceAccessor
  {
    #region Classes

    protected class MountingData
    {
      protected string _cacheKey;
      protected int _usageCount = 0;
      protected IResourceAccessor _baseAccessor;
      protected string _rootDirectoryName;
      protected string _mountPath;

      /// <summary>
      /// Creates a new instance of this class which is based on the given <paramref name="baseAccessor"/>.
      /// </summary>
      /// <param name="cacheKey">Key which is used for this instance.</param>
      /// <param name="baseAccessor">Resource accessor denoting a filesystem resource.</param>
      public MountingData(string cacheKey, IResourceAccessor baseAccessor)
      {
        _cacheKey = cacheKey;
        _baseAccessor = baseAccessor;
        MountResource();
        _inactiveMounts.Add(_cacheKey, this);
      }

      ~MountingData()
      {
        Dispose();
      }

      public void Dispose()
      {
        if (_baseAccessor == null)
          // Already disposed
          return;
        lock (_syncObj)
        {
          _activeMounts.Remove(_cacheKey);
          _inactiveMounts.Remove(_cacheKey);
        }
        UnmountResource();
        _baseAccessor.Dispose();
        _baseAccessor = null;
      }

      #region Protected methods

      protected void MountResource()
      {
        IResourceMountingService resourceMountingService = ServiceRegistration.Get<IResourceMountingService>();
        _rootDirectoryName = Guid.NewGuid().ToString();
        _mountPath = resourceMountingService.CreateRootDirectory(_rootDirectoryName) == null ?
            null : resourceMountingService.AddResource(_rootDirectoryName, _baseAccessor);
      }

      protected void UnmountResource()
      {
        IResourceMountingService resourceMountingService = ServiceRegistration.Get<IResourceMountingService>();
        resourceMountingService.RemoveResource(_rootDirectoryName, _baseAccessor);
        resourceMountingService.DisposeRootDirectory(_rootDirectoryName);
      }

      #endregion

      #region Public members

      public string CacheKey
      {
        get { return _cacheKey; }
      }

      /// <summary>
      /// Returns a resource path which points to the transient local resource provided by this resource access bridge.
      /// </summary>
      public ResourcePath TransientLocalResourcePath
      {
        get { return ResourcePath.BuildBaseProviderPath(LocalFsResourceProviderBase.LOCAL_FS_RESOURCE_PROVIDER_ID, LocalFileSystemPath); }
      }

      public string LocalFileSystemPath
      {
        get { return _mountPath; }
      }

      public void IncUsage()
      {
        lock (_syncObj)
        {
          _usageCount++;
          if (_usageCount > 1)
            return;
          _inactiveMounts.Remove(_cacheKey);
          _activeMounts[_cacheKey] = this;
        }
      }

      public void DecUsage()
      {
        lock (_syncObj)
        {
          _usageCount--;
          if (_usageCount > 0)
            return;
          _activeMounts.Remove(_cacheKey);
          _inactiveMounts.Add(_cacheKey, this);
        }
      }

      public IResourceAccessor ResourceAccessor
      {
        get { return _baseAccessor; }
      }

      public string MountPath
      {
        get { return _mountPath; }
      }

      #endregion

      #region Base overrides

      public override string ToString()
      {
        return "MountData; Root dir = '"  + _rootDirectoryName + "', mount path='" + _mountPath + "', base accessor='" + _baseAccessor + "'";
      }

      #endregion
    }

    #endregion

    #region Consts

    public const int MOUNT_CACHE_SIZE = 20;

    #endregion

    #region Protected fields

    protected static IDictionary<string, MountingData> _activeMounts = new Dictionary<string, MountingData>(); // Maintained by class MountingData
    protected static SmallLRUCache<string, MountingData> _inactiveMounts = new SmallLRUCache<string, MountingData>(MOUNT_CACHE_SIZE); // Maintained by this class
    protected static object _syncObj = _inactiveMounts.SyncObj;

    static StreamedResourceToLocalFsAccessBridge()
    {
      _inactiveMounts.ObjectPruned += OnInactiveMountPruned;
    }

    protected MountingData _mountingData;

    #endregion

    #region Ctor & maintenance

    /// <summary>
    /// Creates a new instance of this class which is based on the given <paramref name="mountingData"/>.
    /// </summary>
    /// <param name="mountingData">Mount this bridge is based on.</param>
    protected StreamedResourceToLocalFsAccessBridge(MountingData mountingData)
    {
      _mountingData = mountingData;
      _mountingData.IncUsage();
    }

    public void Dispose()
    {
      _mountingData.DecUsage();
      _mountingData = null;
    }

    #endregion

    #region Protected members

    static void OnInactiveMountPruned(SmallLRUCache<string, MountingData> sender, string key, MountingData prunedMount)
    {
      prunedMount.Dispose();
    }

    #endregion

    #region Public members

    /// <summary>
    /// Returns a resource accessor instance of interface <see cref="ILocalFsResourceAccessor"/>. This instance will return the
    /// given <paramref name="baseResourceAccessor"/>, casted to <see cref="ILocalFsResourceAccessor"/> if possible, or
    /// a new instance of <see cref="StreamedResourceToLocalFsAccessBridge"/> to provide the <see cref="ILocalFsResourceAccessor"/>
    /// instance.
    /// </summary>
    /// <param name="baseResourceAccessor">Resource accessor which is used to provide the resource contents.</param>
    /// <returns>Resource accessor which implements <see cref="ILocalFsResourceAccessor"/>.</returns>
    public static ILocalFsResourceAccessor GetLocalFsResourceAccessor(IResourceAccessor baseResourceAccessor)
    {
      // Try to get an ILocalFsResourceAccessor
      ILocalFsResourceAccessor result = baseResourceAccessor as ILocalFsResourceAccessor;
      if (result != null)
        // Simple case: The media item is located in the local file system or the resource provider returns
        // an ILocalFsResourceAccessor from elsewhere - simply return it
        return result;
      // Set up a resource bridge mapping the remote or complex resource to a local file or directory
      lock (_inactiveMounts.SyncObj)
      {
        MountingData md;
        string key = baseResourceAccessor.CanonicalLocalResourcePath.Serialize();
        if (_inactiveMounts.TryGetValue(key, out md) || _activeMounts.TryGetValue(key, out md))
          // Base accessor not needed - we use our cached accessor
          baseResourceAccessor.Dispose();
        else
          md = new MountingData(key, baseResourceAccessor);
        return new StreamedResourceToLocalFsAccessBridge(md);
      }
    }

    public static void Shutdown()
    {
      lock (_inactiveMounts.SyncObj)
      {
        foreach (MountingData mountingData in _inactiveMounts.Values)
          mountingData.Dispose();
        _inactiveMounts.Clear();
        foreach (MountingData mountingData in _activeMounts.Values)
          mountingData.Dispose();
        _activeMounts.Clear();
      }
    }

    #endregion

    #region IResourceAccessor implementation

    public IResourceProvider ParentProvider
    {
      get { return null; }
    }

    public bool Exists
    {
      get { return _mountingData.ResourceAccessor.Exists; }
    }

    public bool IsFile
    {
      get { return _mountingData.ResourceAccessor.IsFile; }
    }

    public string ResourceName
    {
      get { return _mountingData.ResourceAccessor.ResourceName; }
    }

    public string ResourcePathName
    {
      get { return _mountingData.ResourceAccessor.ResourcePathName; }
    }

    public ResourcePath CanonicalLocalResourcePath
    {
      get { return _mountingData.ResourceAccessor.CanonicalLocalResourcePath; }
    }

    public DateTime LastChanged
    {
      get { return _mountingData.ResourceAccessor.LastChanged; }
    }

    public long Size
    {
      get { return _mountingData.ResourceAccessor.Size; }
    }

    public bool ResourceExists(string path)
    {
      IFileSystemResourceAccessor fsra = _mountingData.ResourceAccessor as IFileSystemResourceAccessor;
      return fsra == null ? false : fsra.ResourceExists(path);
    }

    public IFileSystemResourceAccessor GetResource(string path)
    {
      IFileSystemResourceAccessor fsra = _mountingData.ResourceAccessor as IFileSystemResourceAccessor;
      return fsra == null ? null : fsra.GetResource(path);
    }

    public void PrepareStreamAccess()
    {
      _mountingData.ResourceAccessor.PrepareStreamAccess();
    }

    public Stream OpenRead()
    {
      return _mountingData.ResourceAccessor.OpenRead();
    }

    public Stream OpenWrite()
    {
      return _mountingData.ResourceAccessor.OpenWrite();
    }

    public IResourceAccessor Clone()
    {
      return new StreamedResourceToLocalFsAccessBridge(_mountingData);
    }

    #endregion

    #region IFileSystemResourceAccessor implementation

    public bool IsDirectory
    {
      get
      {
        IFileSystemResourceAccessor fsra = _mountingData.ResourceAccessor as IFileSystemResourceAccessor;
        return fsra == null ? false : fsra.IsDirectory;
      }
    }

    public ICollection<IFileSystemResourceAccessor> GetFiles()
    {
      IFileSystemResourceAccessor fsra = _mountingData.ResourceAccessor as IFileSystemResourceAccessor;
      return fsra == null ? new List<IFileSystemResourceAccessor>() : fsra.GetFiles();
    }

    public ICollection<IFileSystemResourceAccessor> GetChildDirectories()
    {
      IFileSystemResourceAccessor fsra = _mountingData.ResourceAccessor as IFileSystemResourceAccessor;
      return fsra == null ? new List<IFileSystemResourceAccessor>() : fsra.GetChildDirectories();
    }

    #endregion

    #region ILocalFsResourceAccessor implementation

    public string LocalFileSystemPath
    {
      get
      {
        string result = _mountingData.MountPath;
        if (result == null)
          return null;
        PrepareStreamAccess();
        return result;
      }
    }

    #endregion

    #region Base overrides

    public override string ToString()
    {
      return "StreamedRA access bridge; MountingData = '"  + _mountingData + "'";
    }

    #endregion
  }
}