using Waher.Security;

namespace NeuroAli
{
	public class StdioUser : IUser
	{
		public string UserName => "STDIO";
		public string FederatedUserName => this.UserName;
		public string FriendlyName => this.UserName;
		public string PasswordHash => string.Empty;
		public string PasswordHashType => string.Empty;
		public bool HasPrivilege(string Privilege) => true;
	}
}
