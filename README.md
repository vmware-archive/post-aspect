# post-aspect

## Overview

## Try it out

### Prerequisites

* Requires .Net 4.6.2 or higher
* Library uses Mono Cecil for post compilation

### Build & Run

1. Build PostAspect.sln
2. Reference the assembly or nuget package generated in NugetBuild folder

## Documentation

Given the following sample class.

	public class Sample
	{
	    public string GetString()
	    {
	        return "Hello World";
	    }
	}
	
	public class InterceptAspect : BaseAspect
	{
	    public override void OnEnter(AspectMethodInfo context)
	    {
	    }
	}

It will look like below post compile.	

	public class Sample
	{
	    public string GetString()
	    {
			AspectMethodInfo context;
			aspect.OnEnter(context);
			string local;
			try{
				local = GetString☈();
			}catch(Exception){
				if(context.Rethrow)
					throw;
			}
			finally{
				aspect.Exit(context);
			}
			
			return local;
	    }
		
		public string GetString☈()
	    {
	        return "Hello World";
	    }
	}

## Releases & Major Branches

Coming soon

## Contributing

The post-aspect project team welcomes contributions from the community. Before you start working with post-aspect, please read our [Developer Certificate of Origin](https://cla.vmware.com/dco). All contributions to this repository must be signed as described on that page. Your signature certifies that you wrote the patch or have the right to pass it on as an open-source patch. For more detailed information, refer to [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE.txt)
