create table if not exists hm_inisettings
(
	inisettingid bigserial not null primary key,
	inisettingname varchar (100) not null unique,
	inisettingvalue text not null,
	inisettingfilevalue text not null
);

update hm_dbversion set value = 6011;
