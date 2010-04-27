// MetaTable.cs
//
// Author:
//     Daniel Morgan <danielmorgan@verizon.net>
//
// (C)Copyright 2004 by Daniel Morgan
//
// To be included with Mono as a SQL query tool licensed under the LGPL license.
//

using System;
using System.Data;

namespace Mono.Data.SqlSharp.DatabaseBrowser
{
	public class MetaTable
	{
		private string name = "";
		private string owner = "";

		public MetaTable()
		{
		}

		public MetaTable(string tableName)
		{
			this.name = tableName;
		}

		public MetaTable(string tableOwner, string tableName)
		{
			this.owner = tableOwner;
			this.name = tableName;
		}

		public string Name 
		{
			get 
			{
				return name;
			}
			
			set 
			{
				name = value;
			}
		}

		public string Owner 
		{
			get 
			{
				return owner;
			}

			set 
			{
				owner = value;
			}
		}

		public override string ToString() 
		{
			if(owner != null)
				if(owner.Equals("") == false)
					return owner + "." + name;

			return name;
		}

	}
}

