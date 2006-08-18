//
// attribute.cs: Attribute Handler
//
// Author: Ravi Pratap (ravi@ximian.com)
//         Marek Safar (marek.safar@seznam.cz)
//
// Licensed under the terms of the GNU GPL
//
// (C) 2001 Ximian, Inc (http://www.ximian.com)
//
//

using System;
using System.Diagnostics;
using System.Collections;
using System.Collections.Specialized;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security; 
using System.Security.Permissions;
using System.Text;

namespace Mono.CSharp {

	/// <summary>
	///   Base class for objects that can have Attributes applied to them.
	/// </summary>
	public abstract class Attributable {
		/// <summary>
		///   Attributes for this type
		/// </summary>
 		protected Attributes attributes;

		public Attributable (Attributes attrs)
		{
			attributes = attrs;
		}

		public Attributes OptAttributes 
		{
			get {
				return attributes;
			}
			set {
				attributes = value;
			}
		}

		/// <summary>
		/// Use member-specific procedure to apply attribute @a in @cb to the entity being built in @builder
		/// </summary>
		public abstract void ApplyAttributeBuilder (Attribute a, CustomAttributeBuilder cb);

		/// <summary>
		/// Returns one AttributeTarget for this element.
		/// </summary>
		public abstract AttributeTargets AttributeTargets { get; }

		public abstract bool IsClsCompliaceRequired (DeclSpace ds);

		/// <summary>
		/// Gets list of valid attribute targets for explicit target declaration.
		/// The first array item is default target. Don't break this rule.
		/// </summary>
		public abstract string[] ValidAttributeTargets { get; }
	};

	public class Attribute {
		public readonly string ExplicitTarget;
		public AttributeTargets Target;

		public readonly string    Name;
		public readonly Expression LeftExpr;
		public readonly string Identifier;

		public readonly ArrayList Arguments;

		public readonly Location Location;

		public Type Type;
		
		bool resolve_error;

		// Is non-null if type is AttributeUsageAttribute
		AttributeUsageAttribute usage_attribute;

		public AttributeUsageAttribute UsageAttribute {
			get {
				return usage_attribute;
			}
		}
		
		MethodImplOptions ImplOptions;
		UnmanagedType     UnmanagedType;
		CustomAttributeBuilder cb;
	
		// non-null if named args present after Resolve () is called
		PropertyInfo [] prop_info_arr;
		FieldInfo [] field_info_arr;
		object [] field_values_arr;
		object [] prop_values_arr;
		object [] pos_values;

		static PtrHashtable usage_attr_cache = new PtrHashtable ();
		
		public Attribute (string target, Expression left_expr, string identifier, ArrayList args, Location loc)
		{
			LeftExpr = left_expr;
			Identifier = identifier;
			Name = LeftExpr == null ? identifier : LeftExpr + "." + identifier;
			Arguments = args;
			Location = loc;
			ExplicitTarget = target;
		}

		void Error_InvalidNamedArgument (string name)
		{
			Report.Error (617, Location, "Invalid attribute argument: '{0}'.  Argument must be fields " +
				      "fields which are not readonly, static or const;  or read-write instance properties.",
				      name);
		}

		static void Error_AttributeArgumentNotValid (string extra, Location loc)
		{
			Report.Error (182, loc,
				      "An attribute argument must be a constant expression, typeof " +
				      "expression or array creation expression" + extra);
		}

		static void Error_AttributeArgumentNotValid (Location loc)
		{
			Error_AttributeArgumentNotValid ("", loc);
		}
		
		static void Error_TypeParameterInAttribute (Location loc)
		{
			Report.Error (
				-202, loc, "Can not use a type parameter in an attribute");
		}

		/// <summary>
		/// This is rather hack. We report many emit attribute error with same error to be compatible with
		/// csc. But because csc has to report them this way because error came from ilasm we needn't.
		/// </summary>
		public void Error_AttributeEmitError (string inner)
		{
			Report.Error (647, Location, "Error emitting '{0}' attribute because '{1}'", Name, inner);
		}

		public void Error_InvalidSecurityParent ()
		{
			Error_AttributeEmitError ("it is attached to invalid parent");
		}

		void Error_AttributeConstructorMismatch ()
		{
			Report.Error (-6, Location,
                                      "Could not find a constructor for this argument list.");
		}

		void ResolvePossibleAttributeTypes (EmitContext ec, out Type t1, out Type t2)
		{
			t1 = null;
			t2 = null;

			FullNamedExpression n1 = null;
			FullNamedExpression n2 = null;
			string IdentifierAttribute = Identifier + "Attribute";
			if (LeftExpr == null) {
				n1 = new SimpleName (Identifier, Location).ResolveAsTypeStep (ec);

				// FIXME: Shouldn't do this for quoted attributes: [@A]
				n2 = new SimpleName (IdentifierAttribute, Location).ResolveAsTypeStep (ec);
			} else {
				FullNamedExpression l = LeftExpr.ResolveAsTypeStep (ec);
				if (l == null) {
					Report.Error (246, Location, "Couldn't find namespace or type '{0}'", LeftExpr);
					return;
				}
				n1 = new MemberAccess (l, Identifier, Location).ResolveNamespaceOrType (ec, true);

				// FIXME: Shouldn't do this for quoted attributes: [X.@A]
				n2 = new MemberAccess (l, IdentifierAttribute, Location).ResolveNamespaceOrType (ec, true);
			}

			TypeExpr te1 = n1 == null ? null : n1 as TypeExpr;
			TypeExpr te2 = n2 == null ? null : n2 as TypeExpr;			

			if (te1 != null)
				t1 = te1.ResolveType (ec);
			if (te2 != null)
				t2 = te2.ResolveType (ec);
		}

		/// <summary>
                ///   Tries to resolve the type of the attribute. Flags an error if it can't, and complain is true.
                /// </summary>
		Type CheckAttributeType (EmitContext ec)
		{
			Type t1, t2;
			ResolvePossibleAttributeTypes (ec, out t1, out t2);

			string NameAttribute = Name + "Attribute";

			String err0616 = null;
			if (t1 != null && ! t1.IsSubclassOf (TypeManager.attribute_type)) {
				t1 = null;
				err0616 = "'" + Name + "': is not an attribute class";
			}
			if (t2 != null && ! t2.IsSubclassOf (TypeManager.attribute_type)) {
				t2 = null;
				err0616 = (err0616 != null) 
					? "Neither '" + Name + "' nor '" + NameAttribute +"' is an attribute class"
					: "'" + Name + "Attribute': is not an attribute class";
			}

			if (t1 != null && t2 != null) {
				Report.Error(1614, Location, "'" + Name + "': is ambiguous; " 
					     + " use either '@" + Name + "' or '" + NameAttribute + "'");
				return null;
			}
			if (t1 != null)
				return t1;
			if (t2 != null)
				return t2;
			if (err0616 != null) {
				Report.Error (616, Location, err0616);
				return null;
			}

			Report.Error (246, Location, 
				      "Could not find attribute '" + Name 
				      + "' (are you missing a using directive or an assembly reference ?)");

			resolve_error = true;
			return null;
		}

		public virtual Type ResolveType (EmitContext ec)
		{
			if (Type == null)
				Type = CheckAttributeType (ec);
			return Type;
		}

		/// <summary>
		///   Validates the guid string
		/// </summary>
		bool ValidateGuid (string guid)
		{
			try {
				new Guid (guid);
				return true;
			} catch {
				Report.Error (647, Location, "Format of GUID is invalid: " + guid);
				return false;
			}
		}

		string GetFullMemberName (string member)
		{
			return Type.FullName + '.' + member;
		}

		//
		// Given an expression, if the expression is a valid attribute-argument-expression
		// returns an object that can be used to encode it, or null on failure.
		//
		public static bool GetAttributeArgumentExpression (Expression e, Location loc, Type arg_type, out object result)
		{
			if (e is EnumConstant) {
				if (RootContext.StdLib)
					result = ((EnumConstant)e).GetValueAsEnumType ();
				else
					result = ((EnumConstant)e).GetValue ();

				return true;
			}

			Constant constant = e as Constant;
			if (constant != null) {
				if (e.Type != arg_type) {
					constant = Const.ChangeType (loc, constant, arg_type);
					if (constant == null) {
						result = null;
						Error_AttributeArgumentNotValid (loc);
						return false;
					}
				}
				result = constant.GetValue ();
				return true;
			} else if (e is TypeOf) {
				result = ((TypeOf) e).TypeArg;
				return true;
			} else if (e is ArrayCreation){
				result =  ((ArrayCreation) e).EncodeAsAttribute ();
				if (result != null)
					return true;
			} else if (e is EmptyCast) {
				Expression child = ((EmptyCast)e).Child;
				return GetAttributeArgumentExpression (child, loc, child.Type, out result);
			}

			result = null;
			Error_AttributeArgumentNotValid (loc);
			return false;
		}
		
		public virtual CustomAttributeBuilder Resolve (EmitContext ec)
		{
			if (resolve_error)
				return null;

			resolve_error = true;

			Type oldType = Type;
			
			// Sanity check.
			Type = CheckAttributeType (ec);

			if (oldType == null && Type == null)
				return null;
			if (oldType != null && oldType != Type){
				Report.Error (-27, Location,
					      "Attribute {0} resolved to different types at different times: {1} vs. {2}",
					      Name, oldType, Type);
				return null;
			}

			if (Type.IsAbstract) {
				Report.Error (653, Location, "Cannot apply attribute class '{0}' because it is abstract", Name);
				return null;
			}

			bool MethodImplAttr = false;
			bool MarshalAsAttr = false;
			bool GuidAttr = false;
			bool usage_attr = false;

			bool DoCompares = true;

                        //
                        // If we are a certain special attribute, we
                        // set the information accordingly
                        //
                        
			if (Type == TypeManager.attribute_usage_type)
				usage_attr = true;
			else if (Type == TypeManager.methodimpl_attr_type)
				MethodImplAttr = true;
			else if (Type == TypeManager.marshal_as_attr_type)
				MarshalAsAttr = true;
			else if (Type == TypeManager.guid_attr_type)
				GuidAttr = true;
			else
				DoCompares = false;

			// Now we extract the positional and named arguments
			
			ArrayList pos_args = new ArrayList ();
			ArrayList named_args = new ArrayList ();
			int pos_arg_count = 0;
			
			if (Arguments != null) {
				pos_args = (ArrayList) Arguments [0];
				if (pos_args != null)
					pos_arg_count = pos_args.Count;
				if (Arguments.Count > 1)
					named_args = (ArrayList) Arguments [1];
			}

			pos_values = new object [pos_arg_count];

			//
			// First process positional arguments 
			//

			int i;
			for (i = 0; i < pos_arg_count; i++) {
				Argument a = (Argument) pos_args [i];
				Expression e;

				if (!a.Resolve (ec, Location))
					return null;

				e = a.Expr;

				object val;
				if (!GetAttributeArgumentExpression (e, Location, a.Type, out val))
					return null;
				
				pos_values [i] = val;

				if (DoCompares){
					if (usage_attr) {
						if ((int)val == 0) {
							Report.Error (591, Location, "Invalid value for argument to 'System.AttributeUsage' attribute");
							return null;
						}
						usage_attribute = new AttributeUsageAttribute ((AttributeTargets)val);
					} else if (MethodImplAttr) {
						this.ImplOptions = (MethodImplOptions) val;
					} else if (GuidAttr){
						//
						// we will later check the validity of the type
						//
						if (val is string){
							if (!ValidateGuid ((string) val))
								return null;
						}
						
					} else if (MarshalAsAttr)
						this.UnmanagedType =
						(System.Runtime.InteropServices.UnmanagedType) val;
				}
			}

			//
			// Now process named arguments
			//

			ArrayList field_infos = null;
			ArrayList prop_infos  = null;
			ArrayList field_values = null;
			ArrayList prop_values = null;

			if (named_args.Count > 0) {
				field_infos = new ArrayList ();
				prop_infos  = new ArrayList ();
				field_values = new ArrayList ();
				prop_values = new ArrayList ();
			}

			Hashtable seen_names = new Hashtable();
			
			for (i = 0; i < named_args.Count; i++) {
				DictionaryEntry de = (DictionaryEntry) named_args [i];
				string member_name = (string) de.Key;
				Argument a  = (Argument) de.Value;
				Expression e;

				if (seen_names.Contains(member_name)) {
					Report.Error(643, Location, "'" + member_name + "' duplicate named attribute argument");
					return null;
				}				
				seen_names.Add(member_name, 1);
				
				if (!a.Resolve (ec, Location))
					return null;

				Expression member = Expression.MemberLookup (
					ec, Type, member_name,
					MemberTypes.Field | MemberTypes.Property,
					BindingFlags.Public | BindingFlags.Instance,
					Location);

				if (member == null) {
					member = Expression.MemberLookup (ec, Type, member_name,
						MemberTypes.Field | MemberTypes.Property, BindingFlags.NonPublic | BindingFlags.Instance,
						Location);

					if (member != null) {
						Report.Error (122, Location, "'{0}' is inaccessible due to its protection level", GetFullMemberName (member_name));
						return null;
					}
				}

				if (member == null){
					Report.Error (117, Location, "Attribute `{0}' does not contain a definition for `{1}'",
						      Type, member_name);
					return null;
				}
				
				if (!(member is PropertyExpr || member is FieldExpr)) {
					Error_InvalidNamedArgument (member_name);
					return null;
				}

				e = a.Expr;
				if (e is TypeParameterExpr){
					Error_TypeParameterInAttribute (Location);
					return null;
				}
					
				if (member is PropertyExpr) {
					PropertyExpr pe = (PropertyExpr) member;
					PropertyInfo pi = pe.PropertyInfo;

					if (!pi.CanWrite || !pi.CanRead) {
						Report.SymbolRelatedToPreviousError (pi);
						Error_InvalidNamedArgument (member_name);
						return null;
					}

					object value;
					if (!GetAttributeArgumentExpression (e, Location, pi.PropertyType, out value))
								return null;
						
						if (usage_attribute != null) {
							if (member_name == "AllowMultiple")
							usage_attribute.AllowMultiple = (bool) value;
							if (member_name == "Inherited")
							usage_attribute.Inherited = (bool) value;
					}
					
					prop_values.Add (value);
					prop_infos.Add (pi);
					
				} else if (member is FieldExpr) {
					FieldExpr fe = (FieldExpr) member;
					FieldInfo fi = fe.FieldInfo;

					if (fi.IsInitOnly) {
						Error_InvalidNamedArgument (member_name);
						return null;
					}

 					object value;
 					if (!GetAttributeArgumentExpression (e, Location, fi.FieldType, out value))
						return null;

					field_values.Add (value);

					field_infos.Add (fi);
				}
			}

			Expression mg = Expression.MemberLookup (
				ec, Type, ".ctor", MemberTypes.Constructor,
				BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                                Location);

			if (mg == null) {
				Error_AttributeConstructorMismatch ();
				return null;
			}

			MethodBase constructor = Invocation.OverloadResolve (
				ec, (MethodGroupExpr) mg, pos_args, false, Location);

			if (constructor == null) {
				return null;
			}

			//
			// Now we perform some checks on the positional args as they
			// cannot be null for a constructor which expects a parameter
			// of type object
			//

			ParameterData pd = Invocation.GetParameterData (constructor);

			int last_real_param = pd.Count;
			if (pd.HasParams) {
				// When the params is not filled we need to put one
				if (last_real_param > pos_arg_count) {
					object [] new_pos_values = new object [pos_arg_count + 1];
					pos_values.CopyTo (new_pos_values, 0);
					new_pos_values [pos_arg_count] = new object [] {} ;
					pos_values = new_pos_values;
				}
				last_real_param--;
			}

			for (int j = 0; j < pos_arg_count; ++j) {
				Argument a = (Argument) pos_args [j];
				
				if (a.Expr is NullLiteral && pd.ParameterType (j) == TypeManager.object_type) {
					Error_AttributeArgumentNotValid (Location);
					return null;
				}

				if (j < last_real_param)
					continue;
				
				if (j == last_real_param) {
					object [] array = new object [pos_arg_count - last_real_param];
					array [0] = pos_values [j];
					pos_values [j] = array;
					continue;
				}

				object [] params_array = (object []) pos_values [last_real_param];
				params_array [j - last_real_param] = pos_values [j];
			}

			// Adjust the size of the pos_values if it had params
			if (last_real_param != pos_arg_count) {
				object [] new_pos_values = new object [last_real_param + 1];
				Array.Copy (pos_values, new_pos_values, last_real_param + 1);
				pos_values = new_pos_values;
			}

			try {
				if (named_args.Count > 0) {
					prop_info_arr = new PropertyInfo [prop_infos.Count];
					field_info_arr = new FieldInfo [field_infos.Count];
					field_values_arr = new object [field_values.Count];
					prop_values_arr = new object [prop_values.Count];

					field_infos.CopyTo  (field_info_arr, 0);
					field_values.CopyTo (field_values_arr, 0);

					prop_values.CopyTo  (prop_values_arr, 0);
					prop_infos.CopyTo   (prop_info_arr, 0);

					cb = new CustomAttributeBuilder (
						(ConstructorInfo) constructor, pos_values,
						prop_info_arr, prop_values_arr,
						field_info_arr, field_values_arr);
				}
				else
					cb = new CustomAttributeBuilder (
						(ConstructorInfo) constructor, pos_values);
			} catch (Exception e) {
				//
				// Sample:
				// using System.ComponentModel;
				// [DefaultValue (CollectionChangeAction.Add)]
				// class X { static void Main () {} }
				//
				Error_AttributeArgumentNotValid (Location);
				return null;
			}
			
			resolve_error = false;
			return cb;
		}

		/// <summary>
		///   Get a string containing a list of valid targets for the attribute 'attr'
		/// </summary>
		public string GetValidTargets ()
		{
			StringBuilder sb = new StringBuilder ();
			AttributeTargets targets = GetAttributeUsage ().ValidOn;

			if ((targets & AttributeTargets.Assembly) != 0)
				sb.Append ("'assembly' ");

			if ((targets & AttributeTargets.Class) != 0)
				sb.Append ("'class' ");

			if ((targets & AttributeTargets.Constructor) != 0)
				sb.Append ("'constructor' ");

			if ((targets & AttributeTargets.Delegate) != 0)
				sb.Append ("'delegate' ");

			if ((targets & AttributeTargets.Enum) != 0)
				sb.Append ("'enum' ");

			if ((targets & AttributeTargets.Event) != 0)
				sb.Append ("'event' ");

			if ((targets & AttributeTargets.Field) != 0)
				sb.Append ("'field' ");

			if ((targets & AttributeTargets.Interface) != 0)
				sb.Append ("'interface' ");

			if ((targets & AttributeTargets.Method) != 0)
				sb.Append ("'method' ");

			if ((targets & AttributeTargets.Module) != 0)
				sb.Append ("'module' ");

			if ((targets & AttributeTargets.Parameter) != 0)
				sb.Append ("'parameter' ");

			if ((targets & AttributeTargets.Property) != 0)
				sb.Append ("'property' ");

			if ((targets & AttributeTargets.ReturnValue) != 0)
				sb.Append ("'return' ");

			if ((targets & AttributeTargets.Struct) != 0)
				sb.Append ("'struct' ");

			return sb.ToString ();

		}

		/// <summary>
		/// Returns AttributeUsage attribute for this type
		/// </summary>
		public AttributeUsageAttribute GetAttributeUsage ()
		{
			AttributeUsageAttribute ua = usage_attr_cache [Type] as AttributeUsageAttribute;
			if (ua != null)
				return ua;

			Class attr_class = TypeManager.LookupClass (Type);

			if (attr_class == null) {
				object[] usage_attr = Type.GetCustomAttributes (TypeManager.attribute_usage_type, true);
				ua = (AttributeUsageAttribute)usage_attr [0];
				usage_attr_cache.Add (Type, ua);
				return ua;
			}
		
			return attr_class.AttributeUsage;
		}

		/// <summary>
		/// Returns custom name of indexer
		/// </summary>
		public string GetIndexerAttributeValue (EmitContext ec)
		{
			if (pos_values == null)
				// TODO: It is not neccessary to call whole Resolve (ApplyAttribute does it now) we need only ctor args.
				// But because a lot of attribute class code must be rewritten will be better to wait...
				Resolve (ec);
			
			return pos_values [0] as string;
		}

		/// <summary>
		/// Returns condition of ConditionalAttribute
		/// </summary>
		public string GetConditionalAttributeValue (EmitContext ec)
		{
			if (pos_values == null)
				// TODO: It is not neccessary to call whole Resolve (ApplyAttribute does it now) we need only ctor args.
				// But because a lot of attribute class code must be rewritten will be better to wait...
				Resolve (ec);

			// Some error occurred
			if (pos_values [0] == null)
				return null;

			return (string)pos_values [0];
		}

		/// <summary>
		/// Creates the instance of ObsoleteAttribute from this attribute instance
		/// </summary>
		public ObsoleteAttribute GetObsoleteAttribute (EmitContext ec)
		{
			if (pos_values == null)
				// TODO: It is not neccessary to call whole Resolve (ApplyAttribute does it now) we need only ctor args.
				// But because a lot of attribute class code must be rewritten will be better to wait...
				Resolve (ec);

			// Some error occurred
			if (pos_values == null)
				return null;

			if (pos_values.Length == 0)
				return new ObsoleteAttribute ();

			if (pos_values.Length == 1)
				return new ObsoleteAttribute ((string)pos_values [0]);

			return new ObsoleteAttribute ((string)pos_values [0], (bool)pos_values [1]);
		}

		/// <summary>
		/// Returns value of CLSCompliantAttribute contructor parameter but because the method can be called
		/// before ApplyAttribute. We need to resolve the arguments.
		/// This situation occurs when class deps is differs from Emit order.  
		/// </summary>
		public bool GetClsCompliantAttributeValue (EmitContext ec)
		{
			if (pos_values == null)
				// TODO: It is not neccessary to call whole Resolve (ApplyAttribute does it now) we need only ctor args.
				// But because a lot of attribute class code must be rewritten will be better to wait...
				Resolve (ec);

			// Some error occurred
			if (pos_values [0] == null)
				return false;

			return (bool)pos_values [0];
		}

		/// <summary>
		/// Tests permitted SecurityAction for assembly or other types
		/// </summary>
		public bool CheckSecurityActionValidity (bool for_assembly)
		{
			SecurityAction action  = GetSecurityActionValue ();

			if ((action == SecurityAction.RequestMinimum || action == SecurityAction.RequestOptional || action == SecurityAction.RequestRefuse) && for_assembly)
				return true;

			if (!for_assembly) {
				if (action < SecurityAction.Demand || action > SecurityAction.InheritanceDemand) {
					Error_AttributeEmitError ("SecurityAction is out of range");
					return false;
				}

				if ((action != SecurityAction.RequestMinimum && action != SecurityAction.RequestOptional && action != SecurityAction.RequestRefuse) && !for_assembly)
					return true;
			}

			Error_AttributeEmitError (String.Concat ("SecurityAction '", action, "' is not valid for this declaration"));
			return false;
		}

		System.Security.Permissions.SecurityAction GetSecurityActionValue ()
		{
			return (SecurityAction)pos_values [0];
		}

		/// <summary>
		/// Creates instance of SecurityAttribute class and add result of CreatePermission method to permission table.
		/// </summary>
		/// <returns></returns>
		public void ExtractSecurityPermissionSet (ListDictionary permissions)
		{
			if (TypeManager.LookupDeclSpace (Type) != null && RootContext.StdLib) {
				Error_AttributeEmitError ("security custom attributes can not be referenced from defining assembly");
				return;
			}

			SecurityAttribute sa;
			// For all assemblies except corlib we can avoid all hacks
			if (RootContext.StdLib) {
				sa = (SecurityAttribute) Activator.CreateInstance (Type, pos_values);

				if (prop_info_arr != null) {
					for (int i = 0; i < prop_info_arr.Length; ++i) {
						PropertyInfo pi = prop_info_arr [i];
						pi.SetValue (sa, prop_values_arr [i], null);
					}
				}
			} else {
				Type temp_type = Type.GetType (Type.FullName);
				// HACK: All mscorlib attributes have same ctor syntax
				sa = (SecurityAttribute) Activator.CreateInstance (temp_type, new object[] { GetSecurityActionValue () } );

				// All types are from newly created corlib but for invocation with old we need to convert them
				if (prop_info_arr != null) {
					for (int i = 0; i < prop_info_arr.Length; ++i) {
						PropertyInfo emited_pi = prop_info_arr [i];
						PropertyInfo pi = temp_type.GetProperty (emited_pi.Name, emited_pi.PropertyType);

						object old_instance = pi.PropertyType.IsEnum ?
							System.Enum.ToObject (pi.PropertyType, prop_values_arr [i]) :
							prop_values_arr [i];

						pi.SetValue (sa, old_instance, null);
					}
				}
			}

			IPermission perm;
			try {
				perm = sa.CreatePermission ();
			}
			catch (Exception e) {
				Error_AttributeEmitError (String.Format ("{0} was thrown during attribute processing: {1}", e.GetType (), e.Message));
				return;
			}
			SecurityAction action = GetSecurityActionValue ();

			// IS is correct because for corlib we are using an instance from old corlib
			if (!(perm is System.Security.CodeAccessPermission)) {
				switch (action) {
					case SecurityAction.Demand:
						action = (SecurityAction)13;
						break;
					case SecurityAction.LinkDemand:
						action = (SecurityAction)14;
						break;
					case SecurityAction.InheritanceDemand:
						action = (SecurityAction)15;
						break;
				}
			}

			PermissionSet ps = (PermissionSet)permissions [action];
			if (ps == null) {
				if (sa is PermissionSetAttribute)
					ps = new PermissionSet (sa.Unrestricted ? PermissionState.Unrestricted : PermissionState.None);
				else
					ps = new PermissionSet (PermissionState.None);

				permissions.Add (action, ps);
			} else if (!ps.IsUnrestricted () && sa.Unrestricted) {
				ps = ps.Union (new PermissionSet (PermissionState.Unrestricted));
				permissions [action] = ps;
			}
			ps.AddPermission (perm);
		}

		object GetValue (object value)
		{
			if (value is EnumConstant)
				return ((EnumConstant) value).GetValue ();
			else
				return value;				
		}

		public object GetPositionalValue (int i)
		{
			return (pos_values == null) ? null : pos_values[i];
		}

		object GetFieldValue (string name)
                {
			int i;
			if (field_info_arr == null)
				return null;
			i = 0;
			foreach (FieldInfo fi in field_info_arr) {
				if (fi.Name == name)
					return GetValue (field_values_arr [i]);
				i++;
			}
			return null;
		}

		public UnmanagedMarshal GetMarshal (Attributable attr)
		{
			object value = GetFieldValue ("SizeParamIndex");
			if (value != null && UnmanagedType != UnmanagedType.LPArray) {
				Error_AttributeEmitError ("SizeParamIndex field is not valid for the specified unmanaged type");
				return null;
			}

			object o = GetFieldValue ("ArraySubType");
			UnmanagedType array_sub_type = o == null ? UnmanagedType.I4 : (UnmanagedType) o;
			
			switch (UnmanagedType) {
			case UnmanagedType.CustomMarshaler:
				MethodInfo define_custom = typeof (UnmanagedMarshal).GetMethod ("DefineCustom",
                                                                       BindingFlags.Static | BindingFlags.Public);
				if (define_custom == null) {
					Report.RuntimeMissingSupport (Location, "set marshal info");
					return null;
				}
				
				object [] args = new object [4];
				args [0] = GetFieldValue ("MarshalTypeRef");
				args [1] = GetFieldValue ("MarshalCookie");
				args [2] = GetFieldValue ("MarshalType");
				args [3] = Guid.Empty;
				return (UnmanagedMarshal) define_custom.Invoke (null, args);
				
			case UnmanagedType.LPArray:				
				return UnmanagedMarshal.DefineLPArray (array_sub_type);
			
			case UnmanagedType.SafeArray:
				return UnmanagedMarshal.DefineSafeArray (array_sub_type);
			
			case UnmanagedType.ByValArray:
				FieldMember fm = attr as FieldMember;
				if (fm == null) {
					Error_AttributeEmitError ("Specified unmanaged type is only valid on fields");
					return null;
				}
				return UnmanagedMarshal.DefineByValArray ((int) GetFieldValue ("SizeConst"));
			
			case UnmanagedType.ByValTStr:
				return UnmanagedMarshal.DefineByValTStr ((int) GetFieldValue ("SizeConst"));
			
			default:
				return UnmanagedMarshal.DefineUnmanagedMarshal (UnmanagedType);
			}
		}

		public bool IsInternalCall
		{
			get { return ImplOptions == MethodImplOptions.InternalCall; }
		}

		/// <summary>
		/// Emit attribute for Attributable symbol
		/// </summary>
		public void Emit (EmitContext ec, Attributable ias, ListDictionary emitted_attr)
		{
			CustomAttributeBuilder cb = Resolve (ec);
			if (cb == null)
				return;

			AttributeUsageAttribute usage_attr = GetAttributeUsage ();
			if ((usage_attr.ValidOn & Target) == 0) {
				Report.Error (592, Location, "Attribute '{0}' is not valid on this declaration type. It is valid on {1} declarations only.", Name, GetValidTargets ());
				return;
			}

			ias.ApplyAttributeBuilder (this, cb);

			if (!usage_attr.AllowMultiple) {
				ArrayList emitted_targets = (ArrayList)emitted_attr [Type];
				if (emitted_targets == null) {
					emitted_targets = new ArrayList ();
					emitted_attr.Add (Type, emitted_targets);
				} else if (emitted_targets.Contains (Target)) {
				Report.Error (579, Location, "Duplicate '" + Name + "' attribute");
					return;
				}
				emitted_targets.Add (Target);
			}

			if (!RootContext.VerifyClsCompliance)
				return;

			// Here we are testing attribute arguments for array usage (error 3016)
			if (ias.IsClsCompliaceRequired (ec.DeclSpace)) {
				if (Arguments == null)
					return;

				ArrayList pos_args = (ArrayList) Arguments [0];
				if (pos_args != null) {
					foreach (Argument arg in pos_args) { 
						// Type is undefined (was error 246)
						if (arg.Type == null)
							return;

						if (arg.Type.IsArray) {
							Report.Error (3016, Location, "Arrays as attribute arguments are not CLS-compliant");
							return;
						}
					}
				}
			
				if (Arguments.Count < 2)
					return;
			
				ArrayList named_args = (ArrayList) Arguments [1];
				foreach (DictionaryEntry de in named_args) {
					Argument arg  = (Argument) de.Value;

					// Type is undefined (was error 246)
					if (arg.Type == null)
						return;

					if (arg.Type.IsArray) {
						Report.Error (3016, Location, "Arrays as attribute arguments are not CLS-compliant");
						return;
					}
				}
			}
		}

		public object GetValue (EmitContext ec, Constant c, Type target)
		{
			if (Convert.WideningConversionExists (ec, c, target))
				return c.GetValue ();

			Convert.Error_CannotWideningConversion (Location, c.Type, target);
			return null;
		}
		
		public MethodBuilder DefinePInvokeMethod (EmitContext ec, TypeBuilder builder, string name,
							  MethodAttributes flags, Type ret_type, Type [] param_types)
		{
			//
			// We extract from the attribute the information we need 
			//

			if (Arguments == null) {
				Console.WriteLine ("Internal error : this is not supposed to happen !");
				return null;
			}

			ResolveType (ec);
			if (Type == null)
				return null;
			
			ArrayList named_args = new ArrayList ();
			
			ArrayList pos_args = (ArrayList) Arguments [0];
			if (Arguments.Count > 1)
				named_args = (ArrayList) Arguments [1];
			

			string dll_name = null;
			
			Argument tmp = (Argument) pos_args [0];

			if (!tmp.Resolve (ec, Location))
				return null;
			
			if (tmp.Expr is Constant)
				dll_name = (string) ((Constant) tmp.Expr).GetValue ();
			else { 
				Error_AttributeArgumentNotValid (Location);
				return null;
			}
			if (dll_name == null || dll_name == ""){
				Error_AttributeArgumentNotValid (": DllImport requires a non-empty string", Location);
				return null;
			}
			
			// Now we process the named arguments
			CallingConvention cc = CallingConvention.Winapi;
			CharSet charset = CharSet.Ansi;
			bool preserve_sig = true;
#if FIXME
			bool exact_spelling = false;
#endif
			bool set_last_err = false;
			string entry_point = null;

			for (int i = 0; i < named_args.Count; i++) {

				DictionaryEntry de = (DictionaryEntry) named_args [i];

				string member_name = (string) de.Key;
				Argument a  = (Argument) de.Value;

				if (!a.Resolve (ec, Location))
					return null;

				Expression member = Expression.MemberLookup (
					ec, Type, member_name, 
					MemberTypes.Field | MemberTypes.Property,
					BindingFlags.Public | BindingFlags.Instance,
					Location);

				if (member == null || !(member is FieldExpr)) {
					Error_InvalidNamedArgument (member_name);
					return null;
				}

				if (member is FieldExpr) {
					FieldExpr fe = (FieldExpr) member;
					FieldInfo fi = fe.FieldInfo;

					if (fi.IsInitOnly) {
						Error_InvalidNamedArgument (member_name);
						return null;
					}

					if (a.Expr is Constant) {
						Constant c = (Constant) a.Expr;

						try {
							if (member_name == "CallingConvention"){
								object val = GetValue (ec, c, typeof (CallingConvention));
								if (val == null)
									return null;
								cc = (CallingConvention) val;
							} else if (member_name == "CharSet"){
								charset = (CharSet) c.GetValue ();
							} else if (member_name == "EntryPoint")
								entry_point = (string) c.GetValue ();
							else if (member_name == "SetLastError")
								set_last_err = (bool) c.GetValue ();
#if FIXME
							else if (member_name == "ExactSpelling")
								exact_spelling = (bool) c.GetValue ();
#endif
							else if (member_name == "PreserveSig")
								preserve_sig = (bool) c.GetValue ();
						} catch (InvalidCastException){
							Error_InvalidNamedArgument (member_name);
							Error_AttributeArgumentNotValid (Location);
						}
					} else { 
						Error_AttributeArgumentNotValid (Location);
						return null;
					}
					
				}
			}

			if (entry_point == null)
				entry_point = name;
			if (set_last_err)
				charset = (CharSet)((int)charset | 0x40);
			
			MethodBuilder mb = builder.DefinePInvokeMethod (
				name, dll_name, entry_point, flags | MethodAttributes.HideBySig,
				CallingConventions.Standard,
				ret_type,
				param_types,
				cc,
				charset);

			if (preserve_sig)
				mb.SetImplementationFlags (MethodImplAttributes.PreserveSig);
			
			return mb;
		}

		private Expression GetValue () 
		{
			if ((Arguments == null) || (Arguments.Count < 1))
				return null;
			ArrayList al = (ArrayList) Arguments [0];
			if ((al == null) || (al.Count < 1))
				return null;
			Argument arg = (Argument) al [0];
			if ((arg == null) || (arg.Expr == null))
				return null;
			return arg.Expr;
		}

		public string GetString () 
		{
			Expression e = GetValue ();
			if (e is StringLiteral)
				return (e as StringLiteral).Value;
			return null;
		}

		public bool GetBoolean () 
		{
			Expression e = GetValue ();
			if (e is BoolLiteral)
				return (e as BoolLiteral).Value;
			return false;
		}

		public bool IsAssemblyAttribute {
			get {
				return ExplicitTarget == "assembly";
			}
		}
	
		public bool IsModuleAttribute {
			get {
				return ExplicitTarget == "module";
			}
		}
	}
	

	/// <summary>
	/// For global attributes (assembly, module) we need special handling.
	/// Attributes can be located in the several files
	/// </summary>
	public class GlobalAttribute: Attribute
	{
		public readonly NamespaceEntry ns;

		public GlobalAttribute (TypeContainer container, string target, 
					Expression left_expr, string identifier, ArrayList args, Location loc):
			base (target, left_expr, identifier, args, loc)
		{
			ns = container.NamespaceEntry;
		}

		void Enter ()
		{
			// RootContext.Tree.Types has a single NamespaceEntry which gets overwritten
			// each time a new file is parsed.  However, we need to use the NamespaceEntry
			// in effect where the attribute was used.  Since code elsewhere cannot assume
			// that the NamespaceEntry is right, just overwrite it.
			//
			// Precondition: RootContext.Tree.Types == null

			if (RootContext.Tree.Types.NamespaceEntry != null)
				throw new InternalErrorException (Location + " non-null NamespaceEntry");

			RootContext.Tree.Types.NamespaceEntry = ns;
		}

		void Leave ()
		{
			RootContext.Tree.Types.NamespaceEntry = null;
		}

		public override Type ResolveType (EmitContext ec)
		{
			Enter ();
			Type retval = base.ResolveType (ec);
			Leave ();
			return retval;
		}

		public override CustomAttributeBuilder Resolve (EmitContext ec)
		{
			Enter ();
			CustomAttributeBuilder retval = base.Resolve (ec);
			Leave ();
			return retval;
		}
	}

	public class Attributes {
		public ArrayList Attrs;

		public Attributes (Attribute a)
		{
			Attrs = new ArrayList ();
			Attrs.Add (a);
		}

		public Attributes (ArrayList attrs)
		{
			Attrs = attrs;
		}

		public void AddAttributes (ArrayList attrs)
		{
			Attrs.AddRange (attrs);
		}

		public void AddAttribute (Attribute attr)
		{
			Attrs.Add (attr);
		}

		/// <summary>
		/// Checks whether attribute target is valid for the current element
		/// </summary>
		public bool CheckTargets (Attributable member)
		{
			string[] valid_targets = member.ValidAttributeTargets;
			foreach (Attribute a in Attrs) {
				if (a.ExplicitTarget == null || a.ExplicitTarget == valid_targets [0]) {
					a.Target = member.AttributeTargets;
					continue;
				}

				// TODO: we can skip the first item
				if (((IList) valid_targets).Contains (a.ExplicitTarget)) {
					switch (a.ExplicitTarget) {
						case "return": a.Target = AttributeTargets.ReturnValue; continue;
						case "param": a.Target = AttributeTargets.Parameter; continue;
						case "field": a.Target = AttributeTargets.Field; continue;
						case "method": a.Target = AttributeTargets.Method; continue;
						case "property": a.Target = AttributeTargets.Property; continue;
					}
					throw new InternalErrorException ("Unknown explicit target: " + a.ExplicitTarget);
				}

				StringBuilder sb = new StringBuilder ();
				foreach (string s in valid_targets) {
					sb.Append (s);
					sb.Append (", ");
				}
				sb.Remove (sb.Length - 2, 2);
				Report.Error (657, a.Location, "'{0}' is not a valid attribute location for this declaration. Valid attribute locations for this declaration are '{1}'", a.ExplicitTarget, sb.ToString ());
				return false;
			}
			return true;
		}

		public Attribute Search (Type t, EmitContext ec)
		{
			foreach (Attribute a in Attrs) {
				if (a.ResolveType (ec) == t)
					return a;
			}
			return null;
		}

		/// <summary>
		/// Returns all attributes of type 't'. Use it when attribute is AllowMultiple = true
		/// </summary>
		public Attribute[] SearchMulti (Type t, EmitContext ec)
		{
			ArrayList ar = null;

			foreach (Attribute a in Attrs) {
				if (a.ResolveType (ec) == t) {
					if (ar == null)
						ar = new ArrayList ();
					ar.Add (a);
				}
			}

			return ar == null ? null : ar.ToArray (typeof (Attribute)) as Attribute[];
		}

		public void Emit (EmitContext ec, Attributable ias)
		{
			if (!CheckTargets (ias))
				return;

			ListDictionary ld = new ListDictionary ();

			foreach (Attribute a in Attrs)
				a.Emit (ec, ias, ld);
		}

		public bool Contains (Type t, EmitContext ec)
		{
                        return Search (t, ec) != null;
		}
	}

	/// <summary>
	/// Helper class for attribute verification routine.
	/// </summary>
	sealed class AttributeTester
	{
		static PtrHashtable analyzed_types = new PtrHashtable ();
		static PtrHashtable analyzed_types_obsolete = new PtrHashtable ();
		static PtrHashtable analyzed_member_obsolete = new PtrHashtable ();
		static PtrHashtable analyzed_method_excluded = new PtrHashtable ();

		private AttributeTester ()
		{
		}

		/// <summary>
		/// Returns true if parameters of two compared methods are CLS-Compliant.
		/// It tests differing only in ref or out, or in array rank.
		/// </summary>
		public static bool AreOverloadedMethodParamsClsCompliant (Type[] types_a, Type[] types_b) 
		{
			if (types_a == null || types_b == null)
				return true;

			if (types_a.Length != types_b.Length)
				return true;

			for (int i = 0; i < types_b.Length; ++i) {
				Type aType = types_a [i];
				Type bType = types_b [i];

				if (aType.IsArray && bType.IsArray && aType.GetArrayRank () != bType.GetArrayRank () && aType.GetElementType () == bType.GetElementType ()) {
					return false;
				}

				Type aBaseType = aType;
				bool is_either_ref_or_out = false;

				if (aType.IsByRef || aType.IsPointer) {
					aBaseType = aType.GetElementType ();
					is_either_ref_or_out = true;
				}

				Type bBaseType = bType;
				if (bType.IsByRef || bType.IsPointer) 
				{
					bBaseType = bType.GetElementType ();
					is_either_ref_or_out = !is_either_ref_or_out;
				}

				if (aBaseType != bBaseType)
					continue;

				if (is_either_ref_or_out)
					return false;
			}
			return true;
		}

		/// <summary>
		/// Goes through all parameters and test if they are CLS-Compliant.
		/// </summary>
		public static bool AreParametersCompliant (Parameter[] fixedParameters, Location loc)
		{
			if (fixedParameters == null)
				return true;

			foreach (Parameter arg in fixedParameters) {
				if (!AttributeTester.IsClsCompliant (arg.ParameterType)) {
					Report.Error (3001, loc, "Argument type '{0}' is not CLS-compliant", arg.GetSignatureForError ());
					return false;
				}
			}
			return true;
		}


		/// <summary>
		/// This method tests the CLS compliance of external types. It doesn't test type visibility.
		/// </summary>
		public static bool IsClsCompliant (Type type) 
		{
			if (type == null)
				return true;

			object type_compliance = analyzed_types[type];
			if (type_compliance != null)
				return type_compliance == TRUE;

			if (type.IsPointer) {
				analyzed_types.Add (type, null);
				return false;
			}

			bool result;
			if (type.IsArray || type.IsByRef)	{
				result = IsClsCompliant (TypeManager.GetElementType (type));
			} else {
				result = AnalyzeTypeCompliance (type);
			}
			analyzed_types.Add (type, result ? TRUE : FALSE);
			return result;
		}                

		static object TRUE = new object ();
		static object FALSE = new object ();

		public static void VerifyModulesClsCompliance ()
		{
			Module[] modules = TypeManager.Modules;
			if (modules == null)
				return;

			// The first module is generated assembly
			for (int i = 1; i < modules.Length; ++i) {
				Module module = modules [i];
				if (!IsClsCompliant (module)) {
					Report.Error (3013, "Added modules must be marked with the CLSCompliant attribute to match the assembly", module.Name);
					return;
				}
			}
		}

		/// <summary>
		/// Tests container name for CLS-Compliant name (differing only in case)
		/// </summary>
		public static void VerifyTopLevelNameClsCompliance ()
		{
			Hashtable locase_table = new Hashtable ();

			// Convert imported type names to lower case and ignore not cls compliant
			foreach (DictionaryEntry de in TypeManager.all_imported_types) {
				Type t = (Type)de.Value;
				if (!AttributeTester.IsClsCompliant (t))
					continue;

				locase_table.Add (((string)de.Key).ToLower (System.Globalization.CultureInfo.InvariantCulture), t);
			}

			foreach (DictionaryEntry de in RootContext.Tree.Decls) {
				DeclSpace decl = (DeclSpace)de.Value;
				if (!decl.IsClsCompliaceRequired (decl))
					continue;

				string lcase = decl.Name.ToLower (System.Globalization.CultureInfo.InvariantCulture);
				if (!locase_table.Contains (lcase)) {
					locase_table.Add (lcase, decl);
					continue;
				}

				object conflict = locase_table [lcase];
				if (conflict is Type)
					Report.SymbolRelatedToPreviousError ((Type)conflict);
				else
					Report.SymbolRelatedToPreviousError ((MemberCore)conflict);

				Report.Error (3005, decl.Location, "Identifier '{0}' differing only in case is not CLS-compliant", decl.GetSignatureForError ());
			}
		}

		static bool IsClsCompliant (ICustomAttributeProvider attribute_provider) 
		{
			object[] CompliantAttribute = attribute_provider.GetCustomAttributes (TypeManager.cls_compliant_attribute_type, false);
			if (CompliantAttribute.Length == 0)
				return false;

			return ((CLSCompliantAttribute)CompliantAttribute[0]).IsCompliant;
		}

		static bool AnalyzeTypeCompliance (Type type)
		{
			DeclSpace ds = TypeManager.LookupDeclSpace (type);
			if (ds != null) {
				return ds.IsClsCompliaceRequired (ds.Parent);
			}

			if (type.IsGenericParameter || type.IsGenericInstance)
				return false;

			object[] CompliantAttribute = type.GetCustomAttributes (TypeManager.cls_compliant_attribute_type, false);
			if (CompliantAttribute.Length == 0) 
				return IsClsCompliant (type.Assembly);

			return ((CLSCompliantAttribute)CompliantAttribute[0]).IsCompliant;
		}

		/// <summary>
		/// Returns instance of ObsoleteAttribute when type is obsolete
		/// </summary>
		public static ObsoleteAttribute GetObsoleteAttribute (Type type)
		{
			object type_obsolete = analyzed_types_obsolete [type];
			if (type_obsolete == FALSE)
				return null;

			if (type_obsolete != null)
				return (ObsoleteAttribute)type_obsolete;

			ObsoleteAttribute result = null;
			if (type.IsByRef || type.IsArray || type.IsPointer) {
				result = GetObsoleteAttribute (TypeManager.GetElementType (type));
			} else if (type.IsGenericParameter || type.IsGenericInstance)
				return null;
			else {
				DeclSpace type_ds = TypeManager.LookupDeclSpace (type);

				// Type is external, we can get attribute directly
				if (type_ds == null) {
					object[] attribute = type.GetCustomAttributes (TypeManager.obsolete_attribute_type, false);
					if (attribute.Length == 1)
						result = (ObsoleteAttribute)attribute [0];
				} else {
					result = type_ds.GetObsoleteAttribute (type_ds);
				}
			}

			analyzed_types_obsolete.Add (type, result == null ? FALSE : result);
			return result;
		}

		/// <summary>
		/// Returns instance of ObsoleteAttribute when method is obsolete
		/// </summary>
		public static ObsoleteAttribute GetMethodObsoleteAttribute (MethodBase mb)
		{
			IMethodData mc = TypeManager.GetMethod (mb);
			if (mc != null) 
				return mc.GetObsoleteAttribute ();

			// compiler generated methods are not registered by AddMethod
			if (mb.DeclaringType is TypeBuilder)
				return null;

			PropertyInfo pi = PropertyExpr.AccessorTable [mb] as PropertyInfo;
			if (pi != null)
				return GetMemberObsoleteAttribute (pi);

			return GetMemberObsoleteAttribute (mb);
		}

		/// <summary>
		/// Returns instance of ObsoleteAttribute when member is obsolete
		/// </summary>
		public static ObsoleteAttribute GetMemberObsoleteAttribute (MemberInfo mi)
		{
			object type_obsolete = analyzed_member_obsolete [mi];
			if (type_obsolete == FALSE)
				return null;

			if (type_obsolete != null)
				return (ObsoleteAttribute)type_obsolete;

			if ((mi.DeclaringType is TypeBuilder) || mi.DeclaringType.IsGenericInstance)
				return null;

			ObsoleteAttribute oa = System.Attribute.GetCustomAttribute (mi, TypeManager.obsolete_attribute_type, false) as ObsoleteAttribute;
			analyzed_member_obsolete.Add (mi, oa == null ? FALSE : oa);
			return oa;
		}

		/// <summary>
		/// Common method for Obsolete error/warning reporting.
		/// </summary>
		public static void Report_ObsoleteMessage (ObsoleteAttribute oa, string member, Location loc)
		{
			if (oa.IsError) {
				Report.Error (619, loc, "'{0}' is obsolete: '{1}'", member, oa.Message);
				return;
			}

			if (oa.Message == null) {
				Report.Warning (612, loc, "'{0}' is obsolete", member);
				return;
			}
			if (RootContext.WarningLevel >= 2)
				Report.Warning (618, loc, "'{0}' is obsolete: '{1}'", member, oa.Message);
		}

		public static bool IsConditionalMethodExcluded (MethodBase mb)
		{
			object excluded = analyzed_method_excluded [mb];
			if (excluded != null)
				return excluded == TRUE ? true : false;

			if (mb.Mono_IsInflatedMethod)
				return false;
			
			ConditionalAttribute[] attrs = mb.GetCustomAttributes (TypeManager.conditional_attribute_type, true) as ConditionalAttribute[];
			if (attrs.Length == 0) {
				analyzed_method_excluded.Add (mb, FALSE);
				return false;
			}

			foreach (ConditionalAttribute a in attrs) {
				if (RootContext.AllDefines.Contains (a.ConditionString)) {
					analyzed_method_excluded.Add (mb, FALSE);
					return false;
				}
			}
			analyzed_method_excluded.Add (mb, TRUE);
			return true;
		}
	}
}