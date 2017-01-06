#include "stdafx.h"
#include "Foo.h"


Foo::Foo()
{
}


Foo::~Foo()
{
}


void Foo::Print(int arg)
{
	std::cout << "Hello World #" << arg << std::endl;
}
